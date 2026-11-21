**WrapGCode: a plugin for BamCam to wrap a GCode file for a 4th axis**

**Purpose**:

WrapGCode is intended to be used to create GCode to run on a 4th Axis CNC machine.  This allows the GCode to be created on a 3 axis machine, then converted to a 4-axis machine by replacing all the Xaxis (linear) commands with Aaxis (rotational commands.

**Copyright**:

MIT LIcence, developed by Geoffrey G Roy (geoff@cadplan.com.au)

**Assumptions**:

1. The 4th axis is fixed on the machine bed aligned with the Y-axis, so that the X-axis commands are converted into A-0axis commands.

2. The Z-origin is set to be the centre of rotation of the A-axis

3. The objects used to create the required surface are defined in the X-Y plane and set to a height to match their distance from the centre of rotation (Z=0).

4. The minimum and maximum X values created in the G-Code are used to scale the defined cutting paths to a part or a whole circular shape.

5. Several MOPs can be used, to be executed in the required sequence.

6. The generated G-Code must not contain any G2/G3 (Arc) commands

**Operation:**

1. The MOPs for all the objects used to create the required surface are created in the correct sequence.  The MOPs may be of any type.

2. Set the Postprocessor.Options parameters: "Arc Output"" to "Convert to Lines" and set the "Arc To LInes Tolerance" to a suitable value (default is 0.01), as required. Or, run the "Arcs To Lines" plugin on the entities included in the MOPs making up the surface before generating the G-Code.

3. The 3D-Simulator can be used to view the results of the unwrapped surface.

4. The G-Code is then created and saved into a local file.

5. The plugin is then run to do the wrapping.

6. The plugin asks for three parameter values:
      (a)  The start angle for the wrapping (deg)
      (b)  The finish angle for the wrapping (deg)
      (c)  The direction of wrapping (CW or ACW)

7. The wrapping is completed, and a new GCode file created with a name like: <inputfile>__wrapped.nc.
