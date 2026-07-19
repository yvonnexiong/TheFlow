# The Flow — Story

> The canonical narrative. When the build and this document disagree, this
> document is the intent and the build is behind.

The player is not entering a game. They are witnessing the birth of a world.

Not creating it. Not forcing it. Not mastering it. Only moving in harmony with
what is already becoming.

The experience begins in emptiness. Through stillness, gesture, and resonance,
the player gradually synchronizes with the unfolding of the world — from void,
to earth, to nature, to heaven, and beyond.

## Scene 0 — The Void

Pure white. Silence.

No ground. No sky. No music. No interface.

Only a faint heartbeat.

A single glowing circle appears in the empty space. A voice enters:

> "Before all things, there was only Dao."

## Scene 1 — The Awakening of Earth

The player steps into the circle.

The ground begins to expand outward beneath them. Form spreads softly into the
blankness. A low sound emerges. The first wind is heard.

This is not a world being generated. It is a world awakening. The earth does not
appear all at once. It gently comes into being, as if remembering itself.

## Scene 2 — The First Revelation

In front of the player, a quiet invitation appears: **raise both hands.**

As the player lifts both hands, the space responds. A new world is revealed — a
bamboo forest, emerging like ink made real. Light filters through the stalks.
Mist hangs in the air. Leaves sway with softness and breath.

This is the first living realm the player encounters. Not summoned by power, but
revealed through alignment.

The player begins to understand: when the body moves with stillness, the world
opens.

## Scene 3 — The Flow Between Realms

A new gesture appears. **Circular. Continuous. A flowing loop.**

The player follows the motion with their hands. As the gesture completes, the
bamboo forest begins to transform. The environment dissolves and reforms like
moving ink.

The forest gives way to a **mountain summit**. The air becomes thinner. The
horizon opens. Clouds drift below. The world feels vast and elevated.

The player continues the same circular gesture. Again, the world shifts.

The mountain scene unfolds into a **cosmic realm** — stars, darkness, light, and
distant celestial forms. The player is no longer only within the world. They are
now standing within the greater order beyond it.

From earth, to mountain, to universe — the same flow carries all things.

## Scene 4 — Resonance with the Cosmos

The player continues using the same circular gesture.

Far in the distance, a planet responds. It does not behave like an object being
dragged or manipulated. Instead, it moves as if it is listening.

With each loop of the player's motion, the distant world shifts in resonance —
rotating, drifting, aligning, breathing. The stars pulse softly. Space feels
alive.

Now the player realizes: they were never controlling the world. They were
learning to move with it.

What began as emptiness has become connection. What began as observation has
become harmony.

## Scene 5 — Return to Dao

The player continues the gesture.

The distant planet slows. The stars begin to dim. The cosmic space starts to
dissolve.

Then everything begins to break apart — not into destruction, but into drifting
splats, ink, particles, and fragments of form.

The universe fades. The mountain fades. The bamboo world fades. All the worlds
the player has witnessed begin to disappear back into flowing ink.

These splats gather in the empty space before the player. They swirl. They
converge. They settle.

And slowly, with elegance and weight, they form a single Chinese character:

**道**

Not as flat text, but as a living form made of ink, splats, and breath — as if
the entire journey has returned to its source.

Everything becomes still. The voice returns:

> "All things flow from Dao, and return to Dao."

Silence.

The player understands: the worlds were never separate. Earth, forest, mountain,
cosmos — all were expressions of the same flow. And now, all returns to one.

### Meaning of the ending

This final scene completes the idea that the world is born from emptiness,
unfolds through harmony, and eventually returns to its origin.

The character 道 becomes the ultimate visual summary of the whole experience. It
is not just a symbol at the end — it is the truth that was being revealed the
entire time.

## Ending meaning

The journey is not about power. It is about attunement.

The player does not command creation. They synchronize with its flow.

From void, to earth, to bamboo, to mountain, to cosmos — all things emerge
through the same principle:

**Dao flows through everything. And by moving with it, the player becomes part
of its unfolding.**

---

## Where the build diverges (as of 2026-07-19)

Recorded so the gap is explicit rather than discovered later.

| Scene | Story | Build |
|---|---|---|
| 2 | "Raise **both** hands" | Only the RIGHT rail drives the reveal; the left is tracked but unused |
| 3 | One **circular, continuous** gesture, repeated | Distinct gestures per transition: a curved inward sweep, then an outward parting |
| 4 | Circular gesture; a distant planet responds | Built as 六 resonance — 17 planets on 7 orbits, turned by the sweep gesture |
| **5** | **Worlds dissolve into ink, gather, and settle into 道** | **Not built.** `src/returnToDao.ts` exists but is wired to nothing |
| 0 | Voice line "Before all things, there was only Dao." | Shown as text (`introText.png`); not recorded |
| 5 | Voice line "All things flow from Dao, and return to Dao." | Not recorded |
| 1 | "A low sound emerges. The first wind is heard." | Ground sound plays; no wind |

Two stand out.

**The circular gesture** is the story's through-line for scenes 3 onward — the
*same* motion carrying the player from earth to mountain to cosmos. The build
instead gives each transition its own distinct gesture. Making one repeating
loop the primitive is a design change, not a tuning change.

**Scene 5 is the ending and does not exist.** The journey currently stops at
resonance, which is the second-to-last beat. `src/returnToDao.ts` sketches an
approach — the character rasterised at runtime and sampled into particle
targets, so the form is made of the same drifting stuff as the worlds that
dissolved into it — but nothing calls it.
