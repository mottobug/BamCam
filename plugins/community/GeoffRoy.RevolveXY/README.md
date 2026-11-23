**RevolveXY: a plugin for BamCam to create surfaces of revolution**

**Purpose**:

RevolveXY takes a surface profile shape defined in  the X,Y plane and revolves it a specified angle about the Z axis..

**Copyright**:

MIT LIcence, developed by Geoffrey G Roy (geoff@cadplan.com.au)

**Installation**

The DLL file is placed in the local plugins folder in %AppData%\\Roaming\\BamVam\\plugins, and BamCam restarted.

**Assumptions**:

1. The required shape is defined in the X,Y plane as a open or closed entity.
2. In the plugin this shape is rotated about the Z axis by 90 deg, then swept around the Z axis by the specified angle.

**Operation:**

1. The required profile shape is drawn in the X,Y plane, it can be any shape, open or closed.
2. A single shape is selected then the REvolveXY pluging is selected
3. The user as asked to enter the rotation angle: > 0 and <= 360 degrees
4. A new Surface entity is created.


