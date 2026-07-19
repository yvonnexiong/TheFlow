
import * as THREE from "three";
import {
  EnvironmentType,
  LocomotionEnvironment,
  Mesh,
  MeshBasicMaterial,
  PlaneGeometry,
  SessionMode,
  World,
  // Panel UI temporarily disabled — restore with the block below:
  // Interactable,
  // PanelUI,
  // ScreenSpace,
} from "@iwsdk/core";
// import { PanelSystem } from "./uiPanel.js";
import { GaussianSplatLoader, GaussianSplatLoaderSystem,} from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";
import { SplatMorphSystem } from "./splatMorph.js";
import { SplatRevealSystem } from "./splatReveal.js";
import { DirectorSystem } from "./director.js";


// ------------------------------------------------------------
// Which scene to run
// ------------------------------------------------------------
// "flow"   — The whole journey, one continuous session: 一 breath → 二 reveal
//            → 三 morph, sequenced by the Director (creates/unloads its own
//            splats). This is the real experience.
// "reveal" — Scene 1 only: one world emerges outward from the centre.
// "morph"  — Scene 2 only: two worlds swap as the rail cube scrubs the morph.
//
// The single-scene modes are kept as isolated test/fallback harnesses. Flip
// this to switch what `npm run dev` shows.
const SCENE_MODE: "flow" | "reveal" | "morph" = "flow";


// ------------------------------------------------------------
// Desktop WebXR emulator (IWER) compatibility
// ------------------------------------------------------------
// The IWER dev emulator hands three.js an *emulated* XRSession. three.js's
// WebXRManager decides ONCE, in its constructor (during World.create below),
// whether to use the WebXR Layers path via `supportsGlBinding = typeof
// XRWebGLBinding !== 'undefined'`. Desktop Chrome exposes a *native*
// XRWebGLBinding whose constructor rejects the emulated session
// ("parameter 1 is not of type 'XRSession'"), which aborts XR startup — so
// clicking "Enter XR" silently does nothing.
//
// Removing the global BEFORE the renderer is built forces three.js down the
// XRWebGLLayer base-layer path, which the emulator fully supports. This must
// happen before World.create() — deleting it later is too late, since
// supportsGlBinding is already captured.
//
// Detect the emulator via `window.IWER_DEVICE`, the marker IWER sets when it
// activates. A User-Agent check is NOT usable here: IWER emulates a full Quest
// device and spoofs navigator.userAgent to "OculusBrowser", so on desktop the
// UA falsely looks like a real headset. IWER_DEVICE is the reliable signal and
// is already set by the time this module runs (IWER activates before app code).
//
// This also does the right thing on a real Quest: IWER is inactive there, so
// IWER_DEVICE is undefined, the native binding is kept, and WebXR Layers work.
const iwerDevice = (window as unknown as { IWER_DEVICE?: unknown }).IWER_DEVICE;
if (iwerDevice && "XRWebGLBinding" in window) {
  delete (window as unknown as { XRWebGLBinding?: unknown }).XRWebGLBinding;
  console.warn(
    "[XR] IWER emulator active: removed native XRWebGLBinding so three.js uses " +
      "the XRWebGLLayer base-layer path (native WebXR Layers reject the emulated session).",
  );
}


// ------------------------------------------------------------
// World (IWSDK settings)
// ------------------------------------------------------------
World.create(document.getElementById("scene-container") as HTMLDivElement, {
  assets: {},
  xr: {
    sessionMode: SessionMode.ImmersiveVR,
    offer: "always",
    features: { handTracking: true, layers: true },
  },
  render: {
    defaultLighting: false,
  },
  features: {
    locomotion: true,
    grabbing: true,
    physics: false,
    sceneUnderstanding: false,
  },
})
  .then((world) => {
    world.camera.position.set(0, 1.5, 0);
    world.scene.background = new THREE.Color(0x000000);
    world.scene.add(new THREE.AmbientLight(0xffffff, 1.0));

    world
      // .registerSystem(PanelSystem)   // panel temporarily disabled
      .registerSystem(GaussianSplatLoaderSystem)
      .registerSystem(HandFollowCubeSystem);

    // Registered in separate branches (not a ternary) so each keeps its own
    // concrete system type — registerSystem can't unify two different classes.
    if (SCENE_MODE === "flow") {
      // The Director drives the reveal and morph systems in turn, so all three
      // are registered; the Director creates/unloads scene splats itself.
      world
        .registerSystem(SplatRevealSystem)
        .registerSystem(SplatMorphSystem)
        .registerSystem(DirectorSystem);
    } else if (SCENE_MODE === "reveal") {
      world.registerSystem(SplatRevealSystem);
    } else {
      world.registerSystem(SplatMorphSystem);
    }


    // ------------------------------------------------------------
    // Gaussian Splat worlds (hand-morphed — see splatMorph.ts)
    // ------------------------------------------------------------
    // Filenames contain spaces, so each must be percent-encoded — an
    // unencoded space makes the fetch 404 and the load silently time out.
    // Encode each path segment so spaces survive AND subfolder "/" is preserved
    // (encodeURIComponent on the whole string would turn "/" into "%2F" → 404).
    const splatUrl = (p: string) =>
      "./splats/" + p.split("/").map(encodeURIComponent).join("/");

    // animate: false — the fly-in animator would fight the reveal/morph modifier
    // for control of opacity. Scenes start fully materialised; the modifier
    // decides what's visible.
    if (SCENE_MODE === "flow") {
      // The Director creates and unloads its own scene splats per phase —
      // nothing to pre-create here.
    } else if (SCENE_MODE === "reveal") {
      // Scene 1 (一 → 二): a single world emerges outward from the centre as
      // the hand moves.
      const scene1 = world.createTransformEntity();
      scene1.addComponent(GaussianSplatLoader, {
        splatUrl: splatUrl("Scene1/Celestial Pathways Amidst Clouds.compressed.ply"),
        animate: false,
      });

      const reveal = world.getSystem(SplatRevealSystem)!;
      reveal.setScene(scene1);
      reveal.grow(5); // auto-grow once loaded, so this mode is testable at a desk
    } else {
      // Scene 2 (二): two worlds swap as the rail cube scrubs the morph.
      const sceneA = world.createTransformEntity();
      sceneA.addComponent(GaussianSplatLoader, {
        splatUrl: splatUrl("Scene1_Ancient Chinese Bamboo Courtyard.spz"),
        animate: false,
      });

      const sceneB = world.createTransformEntity();
      sceneB.addComponent(GaussianSplatLoader, {
        splatUrl: splatUrl("Scene2_Ruined Sanctuary Apocalyptic Aftermath.spz"),
        animate: false,
      });

      world.getSystem(SplatMorphSystem)!.setScenes(sceneA, sceneB);
    }


    // ------------------------------------------------------------
    // Invisible floor for locomotion (must be a Mesh for IWSDK raycasting)
    // ------------------------------------------------------------
    const floorGeometry = new PlaneGeometry(100, 100);
    floorGeometry.rotateX(-Math.PI / 2);
    const floor = new Mesh(floorGeometry, new MeshBasicMaterial());
    floor.visible = false;
    world
      .createTransformEntity(floor)
      .addComponent(LocomotionEnvironment, { type: EnvironmentType.STATIC });

    const grid = new THREE.GridHelper(100, 100, 0x444444, 0x222222);
    grid.material.transparent = true;
    grid.material.opacity = 0.4;
    world.scene.add(grid);


    // ------------------------------------------------------------
    // TEMPORARILY DISABLED — Sensai Panel UI
    // ------------------------------------------------------------
    // Restore by uncommenting this block, re-adding `.registerSystem(PanelSystem)`
    // above, and un-commenting the PanelSystem / PanelUI / Interactable /
    // ScreenSpace imports at the top of this file.
    //
    // const panelEntity = world
    //   .createTransformEntity()
    //   .addComponent(PanelUI, {
    //     config: "./ui/sensai.json",
    //     maxHeight: 0.8,
    //     maxWidth: 1.6,
    //   })
    //   .addComponent(Interactable)
    //   .addComponent(ScreenSpace, {
    //     top: "30%",
    //     bottom: "30%",
    //     left: "30%",
    //     right: "30%",
    //     height: "40%",
    //     width: "40%",
    //   });
    // panelEntity.object3D!.position.set(0, 1.29, -2.9);


    // ------------------------------------------------------------
    // TEMPORARY — DOM "Enter XR" button
    // ------------------------------------------------------------
    // The panel above carried the only Enter XR control, so removing it would
    // otherwise leave no way into the session. Delete this block when the
    // panel comes back.
    const xrButton = document.createElement("button");
    xrButton.textContent = "Enter XR";
    xrButton.style.cssText =
      "position:fixed;top:16px;left:16px;z-index:9999;padding:12px 20px;" +
      "font:600 16px system-ui,sans-serif;color:#fff;background:#0088cc;" +
      "border:none;border-radius:8px;cursor:pointer";
    xrButton.addEventListener("click", () => {
      Promise.resolve(world.launchXR()).catch((err) => {
        console.error("[World] launchXR() failed:", err);
      });
    });
    document.body.appendChild(xrButton);

  })
  .catch((err) => {
    console.error("[World] Failed to create the IWSDK world:", err);
    const container = document.getElementById("scene-container");
  });

  
