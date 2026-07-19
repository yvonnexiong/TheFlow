"""
mesh2splat_py — GLB -> 3DGS PLY (standard INRIA format).

Implements the Mesh2Splat approach (EA SEED, Scolari 2025) in Python:
surface-aligned flat Gaussians sampled over the mesh, colored from the
glTF baseColor texture, emitted as canonical 3DGS PLY.

Differences from the EA GPU tool: samples uniformly over 3D surface area
(rather than per-fragment in UV space, which ties density to texel density),
and carries only baseColor -- no metallic/roughness/normal-map channels.
"""

import argparse
import io
import json
import struct
import sys

import numpy as np
from PIL import Image

SH_C0 = 0.28209479177387814

# ---------------------------------------------------------------- GLB parsing

COMPONENT_DTYPES = {
    5120: np.int8, 5121: np.uint8, 5122: np.int16,
    5123: np.uint16, 5125: np.uint32, 5126: np.float32,
}
TYPE_COUNTS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def load_glb(path):
    with open(path, "rb") as f:
        magic, version, _ = struct.unpack("<III", f.read(12))
        if magic != 0x46546C67:
            raise ValueError(f"not a GLB file: {path}")
        if version != 2:
            raise ValueError(f"unsupported glTF version {version}")
        gltf, buffers = None, b""
        while True:
            header = f.read(8)
            if len(header) < 8:
                break
            clen, ctype = struct.unpack("<II", header)
            data = f.read(clen)
            if ctype == 0x4E4F534A:      # JSON
                gltf = json.loads(data)
            elif ctype == 0x004E4942:    # BIN
                buffers = data
    if gltf is None:
        raise ValueError("GLB has no JSON chunk")
    return gltf, buffers


def read_accessor(gltf, blob, index):
    acc = gltf["accessors"][index]
    dtype = COMPONENT_DTYPES[acc["componentType"]]
    ncomp = TYPE_COUNTS[acc["type"]]
    count = acc["count"]

    if "bufferView" not in acc:
        return np.zeros((count, ncomp), dtype=dtype)

    bv = gltf["bufferViews"][acc["bufferView"]]
    base = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    itemsize = np.dtype(dtype).itemsize
    stride = bv.get("byteStride") or (itemsize * ncomp)

    if stride == itemsize * ncomp:
        arr = np.frombuffer(blob, dtype=dtype, count=count * ncomp, offset=base)
        arr = arr.reshape(count, ncomp)
    else:
        # interleaved: gather each element from its stride slot
        raw = np.frombuffer(blob, dtype=np.uint8, offset=base, count=stride * count)
        raw = raw.reshape(count, stride)[:, : itemsize * ncomp]
        arr = raw.copy().view(dtype).reshape(count, ncomp)

    if acc.get("normalized") and dtype != np.float32:
        arr = arr.astype(np.float32) / np.iinfo(dtype).max
    return arr


def node_transform(node):
    if "matrix" in node:
        # glTF matrices are column-major
        return np.array(node["matrix"], dtype=np.float64).reshape(4, 4).T
    m = np.eye(4)
    if "scale" in node:
        m = np.diag(list(node["scale"]) + [1.0]) @ m
    if "rotation" in node:
        x, y, z, w = node["rotation"]
        r = np.array([
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ])
        rm = np.eye(4)
        rm[:3, :3] = r
        m = rm @ m
    if "translation" in node:
        tm = np.eye(4)
        tm[:3, 3] = node["translation"]
        m = tm @ m
    return m


def collect_primitives(gltf, blob):
    """Walk the scene graph, returning primitives with world-space geometry."""
    out = []

    def visit(node_idx, parent):
        node = gltf["nodes"][node_idx]
        world = parent @ node_transform(node)
        if "mesh" in node:
            for prim in gltf["meshes"][node["mesh"]]["primitives"]:
                if prim.get("mode", 4) != 4:
                    continue  # triangles only
                attrs = prim["attributes"]
                if "POSITION" not in attrs:
                    continue
                pos = read_accessor(gltf, blob, attrs["POSITION"]).astype(np.float64)
                pos = pos @ world[:3, :3].T + world[:3, 3]

                if "NORMAL" in attrs:
                    nrm = read_accessor(gltf, blob, attrs["NORMAL"]).astype(np.float64)
                    nrm = nrm @ np.linalg.inv(world[:3, :3]).T
                else:
                    nrm = None

                uv = (read_accessor(gltf, blob, attrs["TEXCOORD_0"]).astype(np.float64)
                      if "TEXCOORD_0" in attrs else None)

                if "indices" in prim:
                    idx = read_accessor(gltf, blob, prim["indices"]).ravel().astype(np.int64)
                else:
                    idx = np.arange(len(pos), dtype=np.int64)

                out.append({
                    "pos": pos, "nrm": nrm, "uv": uv,
                    "tri": idx.reshape(-1, 3),
                    "material": prim.get("material"),
                })
        for child in node.get("children", []):
            visit(child, world)

    scene = gltf.get("scene", 0)
    roots = gltf.get("scenes", [{}])[scene].get("nodes", range(len(gltf.get("nodes", []))))
    for r in roots:
        visit(r, np.eye(4))
    return out


def load_material_texture(gltf, blob, mat_index):
    """Return (image_array HxWx3 float 0..1, baseColorFactor rgba) for a material."""
    factor = np.ones(4)
    if mat_index is None or "materials" not in gltf:
        return None, factor
    mat = gltf["materials"][mat_index]
    pbr = mat.get("pbrMetallicRoughness", {})
    factor = np.array(pbr.get("baseColorFactor", [1, 1, 1, 1]), dtype=np.float64)

    tex_info = pbr.get("baseColorTexture")
    if tex_info is None:
        return None, factor

    tex = gltf["textures"][tex_info["index"]]
    if "source" not in tex:
        return None, factor
    img_def = gltf["images"][tex["source"]]
    if "bufferView" not in img_def:
        return None, factor

    bv = gltf["bufferViews"][img_def["bufferView"]]
    start = bv.get("byteOffset", 0)
    raw = blob[start:start + bv["byteLength"]]
    img = Image.open(io.BytesIO(raw)).convert("RGB")
    return np.asarray(img, dtype=np.float64) / 255.0, factor


def sample_texture(img, uvs):
    """Bilinear sample with wrap addressing. uvs: (N,2) -> (N,3) linear-ish RGB."""
    h, w, _ = img.shape
    u = np.mod(uvs[:, 0], 1.0) * w - 0.5
    v = np.mod(uvs[:, 1], 1.0) * h - 0.5
    x0 = np.floor(u).astype(np.int64)
    y0 = np.floor(v).astype(np.int64)
    fx = (u - x0)[:, None]
    fy = (v - y0)[:, None]
    x0m, x1m = np.mod(x0, w), np.mod(x0 + 1, w)
    y0m, y1m = np.mod(y0, h), np.mod(y0 + 1, h)
    c = (img[y0m, x0m] * (1 - fx) * (1 - fy) + img[y0m, x1m] * fx * (1 - fy)
         + img[y1m, x0m] * (1 - fx) * fy + img[y1m, x1m] * fx * fy)
    return c


# ------------------------------------------------------------------- sampling

def sample_primitive(prim, n_samples, rng):
    """Area-weighted uniform surface sampling. Returns positions, normals, uvs, areas."""
    pos, tri = prim["pos"], prim["tri"]
    a, b, c = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
    cross = np.cross(b - a, c - a)
    areas = 0.5 * np.linalg.norm(cross, axis=1)
    total = areas.sum()
    if total <= 0 or n_samples <= 0:
        return None

    # pick triangles proportional to area
    cdf = np.cumsum(areas) / total
    picks = np.searchsorted(cdf, rng.random(n_samples))
    picks = np.clip(picks, 0, len(tri) - 1)

    # uniform barycentric coords
    r1, r2 = rng.random(n_samples), rng.random(n_samples)
    s = np.sqrt(r1)
    w0 = (1 - s)[:, None]
    w1 = (s * (1 - r2))[:, None]
    w2 = (s * r2)[:, None]

    t = tri[picks]
    P = pos[t[:, 0]] * w0 + pos[t[:, 1]] * w1 + pos[t[:, 2]] * w2

    if prim["nrm"] is not None:
        nrm = prim["nrm"]
        N = nrm[t[:, 0]] * w0 + nrm[t[:, 1]] * w1 + nrm[t[:, 2]] * w2
    else:
        N = cross[picks]
    n_len = np.linalg.norm(N, axis=1, keepdims=True)
    N = np.divide(N, n_len, out=np.tile([0.0, 0.0, 1.0], (len(N), 1)), where=n_len > 1e-12)

    if prim["uv"] is not None:
        uv = prim["uv"]
        UV = uv[t[:, 0]] * w0 + uv[t[:, 1]] * w1 + uv[t[:, 2]] * w2
    else:
        UV = None

    return P, N, UV, total


def normals_to_quats(N):
    """Quaternion (w,x,y,z) rotating +Z onto each normal -- flat axis along normal."""
    z = np.array([0.0, 0.0, 1.0])
    d = N @ z
    axis = np.cross(np.tile(z, (len(N), 1)), N)
    axis_len = np.linalg.norm(axis, axis=1)

    q = np.zeros((len(N), 4))
    q[:, 0] = 1.0 + d
    q[:, 1:] = axis

    # antiparallel case: 180deg about any perpendicular axis
    flip = (axis_len < 1e-9) & (d < 0)
    if flip.any():
        q[flip] = [0.0, 1.0, 0.0, 0.0]

    q /= np.linalg.norm(q, axis=1, keepdims=True)
    return q


# --------------------------------------------------------------- PLY emission

def write_standard_3dgs_ply(path, xyz, normals, colors, opacity, scales, quats):
    n = len(xyz)
    header = ["ply", "format binary_little_endian 1.0", f"element vertex {n}"]
    header += [f"property float {c}" for c in ("x", "y", "z", "nx", "ny", "nz")]
    header += [f"property float f_dc_{i}" for i in range(3)]
    header += [f"property float f_rest_{i}" for i in range(45)]
    header += ["property float opacity"]
    header += [f"property float scale_{i}" for i in range(3)]
    header += [f"property float rot_{i}" for i in range(4)]
    header += ["end_header", ""]

    data = np.zeros((n, 62), dtype=np.float32)
    data[:, 0:3] = xyz
    data[:, 3:6] = normals
    data[:, 6:9] = colors          # f_dc
    # 9:54 -> f_rest, left zero (view-independent albedo)
    data[:, 54] = opacity
    data[:, 55:58] = scales
    data[:, 58:62] = quats

    with open(path, "wb") as f:
        f.write("\n".join(header).encode("ascii"))
        f.write(data.tobytes())
    return n


# --------------------------------------------------------------------- driver

def main():
    ap = argparse.ArgumentParser(description="Convert a GLB mesh into a 3DGS PLY.")
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--splats", type=int, default=400_000,
                    help="target total gaussian count")
    ap.add_argument("--sigma", type=float, default=0.65,
                    help="gaussian std vs sample spacing (EA default 0.65)")
    ap.add_argument("--thinness", type=float, default=0.05,
                    help="flat-axis scale as fraction of in-plane scale")
    ap.add_argument("--opacity", type=float, default=0.99)
    ap.add_argument("--linearize", action="store_true",
                    help="sRGB->linear before encoding DC (EA tool does NOT do this)")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    rng = np.random.default_rng(args.seed)
    gltf, blob = load_glb(args.input)
    prims = collect_primitives(gltf, blob)
    if not prims:
        sys.exit("no triangle primitives found")

    # distribute the splat budget across primitives by surface area
    areas = []
    for p in prims:
        a, b, c = p["pos"][p["tri"][:, 0]], p["pos"][p["tri"][:, 1]], p["pos"][p["tri"][:, 2]]
        areas.append(0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1).sum())
    areas = np.array(areas)
    total_area = areas.sum()
    print(f"{len(prims)} primitive(s), total surface area {total_area:.4f}")

    all_xyz, all_nrm, all_col, all_scale, all_quat = [], [], [], [], []

    for p, area in zip(prims, areas):
        n_s = int(round(args.splats * area / total_area))
        if n_s <= 0:
            continue
        result = sample_primitive(p, n_s, rng)
        if result is None:
            continue
        P, N, UV, _ = result

        img, factor = load_material_texture(gltf, blob, p["material"])
        if img is not None and UV is not None:
            rgb = sample_texture(img, UV) * factor[:3]
        else:
            rgb = np.tile(factor[:3], (len(P), 1))

        # mean sample spacing on this surface -> in-plane gaussian extent
        spacing = np.sqrt(area / n_s)
        s_plane = spacing * args.sigma
        scales = np.tile([s_plane, s_plane, s_plane * args.thinness], (len(P), 1))

        all_xyz.append(P)
        all_nrm.append(N)
        all_col.append(rgb)
        all_scale.append(scales)
        all_quat.append(normals_to_quats(N))
        print(f"  prim: {len(P)} splats, spacing {spacing:.5f}, "
              f"texture {'yes' if img is not None else 'no'}")

    xyz = np.concatenate(all_xyz)
    nrm = np.concatenate(all_nrm)
    rgb = np.concatenate(all_col)
    scales = np.concatenate(all_scale)
    quats = np.concatenate(all_quat)

    # Texel -> SH DC coefficient. Mesh2Splat samples albedo from a non-sRGB
    # (GL_RGBA8) texture and feeds the raw value straight into
    # (color - 0.5) / SH_C0, so no linearization by default.
    if args.linearize:
        rgb = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    f_dc = (rgb - 0.5) / SH_C0

    opa = np.full(len(xyz), np.log(args.opacity / (1 - args.opacity)), dtype=np.float64)
    log_scales = np.log(np.maximum(scales, 1e-12))

    n = write_standard_3dgs_ply(args.output, xyz, nrm, f_dc, opa, log_scales, quats)
    lo, hi = xyz.min(axis=0), xyz.max(axis=0)
    print(f"\nwrote {n} splats -> {args.output}")
    print(f"bounds min {np.round(lo, 4)} max {np.round(hi, 4)}")


if __name__ == "__main__":
    main()
