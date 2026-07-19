// Convert a standard 3DGS PLY into SPZ using SparkJS's own SpzWriter, so the
// SPZ version always matches the pinned Spark build that will read it.
import { readFileSync, writeFileSync } from "node:fs";
import { SpzWriter } from "@sparkjsdev/spark";

const [, , inPath, outPath] = process.argv;
if (!inPath || !outPath) {
  console.error("usage: node ply2spz.mjs <input.ply> <output.spz>");
  process.exit(1);
}

const buf = readFileSync(inPath);

// ---- parse header -------------------------------------------------------
const headEnd = buf.indexOf("end_header\n");
if (headEnd < 0) throw new Error("no end_header found");
const header = buf.subarray(0, headEnd).toString("ascii");
const dataStart = headEnd + "end_header\n".length;

if (!/format binary_little_endian/.test(header)) {
  throw new Error("only binary_little_endian PLY is supported");
}
const numSplats = Number(header.match(/element vertex (\d+)/)[1]);
const props = [...header.matchAll(/property\s+(\w+)\s+(\w+)/g)].map((m) => ({
  type: m[1],
  name: m[2],
}));
if (props.some((p) => p.type !== "float")) {
  throw new Error("expected an all-float property layout");
}

const idx = Object.fromEntries(props.map((p, i) => [p.name, i]));
const stride = props.length;
const need = ["x", "y", "z", "f_dc_0", "opacity", "scale_0", "rot_0"];
for (const k of need) {
  if (!(k in idx)) throw new Error(`PLY missing required property '${k}'`);
}

const expected = dataStart + numSplats * stride * 4;
if (buf.length < expected) {
  throw new Error(`truncated PLY: need ${expected} bytes, have ${buf.length}`);
}

const f32 = new Float32Array(
  buf.buffer.slice(buf.byteOffset + dataStart, buf.byteOffset + expected),
);
console.log(`${numSplats} splats, ${stride} properties each`);

// ---- transcode ----------------------------------------------------------
const SH_C0 = 0.28209479177387814;
const sigmoid = (v) => 1 / (1 + Math.exp(-v));

const writer = new SpzWriter({ numSplats, shDegree: 0, flagAntiAlias: true });

for (let i = 0; i < numSplats; i++) {
  const o = i * stride;

  writer.setCenter(i, f32[o + idx.x], f32[o + idx.y], f32[o + idx.z]);

  // DC coefficient -> 0..1 colour, which is what setRgb expects
  writer.setRgb(
    i,
    0.5 + SH_C0 * f32[o + idx.f_dc_0],
    0.5 + SH_C0 * f32[o + idx.f_dc_1],
    0.5 + SH_C0 * f32[o + idx.f_dc_2],
  );

  writer.setAlpha(i, sigmoid(f32[o + idx.opacity]));

  writer.setScale(
    i,
    Math.exp(f32[o + idx.scale_0]),
    Math.exp(f32[o + idx.scale_1]),
    Math.exp(f32[o + idx.scale_2]),
  );

  // PLY stores rot as (w,x,y,z); setQuat takes (x,y,z,w)
  writer.setQuat(
    i,
    f32[o + idx.rot_1],
    f32[o + idx.rot_2],
    f32[o + idx.rot_3],
    f32[o + idx.rot_0],
  );
}

const bytes = await writer.finalize();
writeFileSync(outPath, bytes);
console.log(
  `wrote ${outPath}  ${(bytes.length / 1e6).toFixed(2)} MB` +
    (writer.clippedCount ? `  (${writer.clippedCount} values clipped)` : ""),
);
