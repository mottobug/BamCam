**RevolveXY: a plugin for BamCam to create surfaces of revolution**

**Purpose**:

RevolveXY takes a surface profile shape defined in the X,Y plane and revolves it a specified angle about the Z axis.

**Copyright**:

MIT Licence, developed by Geoffrey G Roy (geoff@cadplan.com.au)

**Installation**

Install the release ZIP `community-GeoffRoy.RevolveXY-<version>.zip` with **Tools ▸ Plugins ▸ Install from ZIP...**, or
place the DLL in the plugins folder (Windows: `%APPDATA%\BamCam\plugins`), and restart BamCam.

**Assumptions**:

1. The required shape is defined in the X,Y plane as an open or closed entity.
2. In the plugin this shape is turned into the X,Z plane (90 deg about the X axis: x, y becomes x, 0, y), then swept
   around the Z axis by the specified angle. X is the radius, Y the height.

**Operation:**

1. The required profile shape is drawn in the X,Y plane, it can be any shape, open or closed.
2. A single shape is selected, then **Plugins ▸ Revolve XY...** is chosen.
3. The user is asked to enter the rotation angle: > 0 and <= 360 degrees.
4. A new Surface entity is created.
