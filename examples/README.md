# Examples

One drawing per kind of operation, each with as many of its options as fit. Open one, press `Ctrl+T` to generate
the toolpaths and look at them in the drawing or in the 3D simulation (`Ctrl+Shift+T`). The names of the operations
say what each one shows.

Everything in these files is made by BamCam's tests (`tests/BamCam.Tests/ExampleDrawingsTests.cs` and
`PebbleExampleTests.cs`): shapes, texts, the 3D model and the G-code of the NC file. They contain nothing from
third parties. The tests check that every operation in them makes its toolpath without a warning; with
`BAMCAM_WRITE_EXAMPLES=1` they write the files again.

| File | Operations and options |
|---|---|
| `Profile.cb` | Outline roughing with holding tabs and a tangent lead-in, finishing in one climb pass; a star inside with corner overcut and spiral lead-in; a slot along an open line with a cut width; a hole with a chamfer (side profile) |
| `Pocket.cb` | Pocket round two islands with spiral lead-in and finish stepover at the bottom; horizontal hatching; three pockets cut from the start point, nearest first; inside offsets round a core |
| `Engrave.cb` | Engraving a text and an open wave line in two passes |
| `V-Engrave.cb` | Lettering with a 60° bit; a star with a flat bottom cleared with the flat stepover |
| `Drilling.cb` | Peck drilling (G83), spot drilling with a dwell (G82), holes spiral milled clockwise with a flat base and lead-out, a counterbore milled counter-clockwise |
| `Thread milling and tapping.cb` | Internal thread bottom up, a left-hand thread top down in two radial passes, an external thread on a boss, tapping (G84) |
| `Drag knife.cb` | Lettering, a star and a ring with knife offset, swivel angle and overcut |
| `Plasma.cb` | A bracket with holes, a slot and a small hole: arc lead-in, line lead-out, overcut, probing, THC, slower small holes, a start point per shape, values entered without cut data |
| `Turning.cb` | Facing, roughing and finishing with constant surface speed (G96), a relief groove, a thread M16 x 2, drilling with pecks and boring (internal turning) |
| `Trochoidal milling (matmill).cb` | Trochoidal pocket round an island (mixed direction), an outside slot with smooth chords, inside with the inside corners cleared |
| `NC file.cb` | A G-code file (`NC file (spiral).nc`) put into the job before a profile |
| `Sketch (mounting plate).cb` | Two parametric sketches (double-click to edit): a plate with rounded corners and its holes, sized by the variables `Width`, `Height`, `Corner`, `Edge`, `Hole` and `Bore`; an inside profile for the holes, an outside profile with tabs for the outline |
| `3D profile (pebble).cb` | Trochoidal and waterline roughing, rest roughing, waterline and scanline finishing, the back face with the part turned over, and the mould of it |
