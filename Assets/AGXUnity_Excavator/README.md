# AGXUnity_Excavator.unity
---------------------------
This scene demonstrates an excavator in an earth moving scenario.

The Excavator is created in Algoryx Momentum, a Dynamics plugin for SpaceClaim: https://www.algoryx.se/momentum/
After exporting the dynamics model, it is imported into AGXUnity as a prefab.

The Tracks (left/right) is added to a parent GameComponent to the Excavator prefab.

Excavator.cs and ExcavatorInputController.cs is added to model the drivetrain and to control the various actuators on the Excavator.
The drivetrain consists of a combustion engine, gears, clutch and brakes. For more details see Excavator.cs

The editor exposes various parameters for the engine and the drivetrain.

## Control the excavator

### Gamepad (XBox360)
- Right Stick X     - Boom up/down
- Right Stick Y     - Move bucket
- Left Stick X      - Swing left/right
- Left Stick Y      - Stick up/down
- D-Pad (X/Y)       - Drive forward/backward/left/right


### Keyboard
- PageUp/Down       - Boom up/down
- Insert/Delete     - Move bucket
- T/U               - Swing left/right
- Home/End          - Stick up/down
- Up/Down           - Forward/Backward
- Left/Right        - Turn Left/Right


# AGXUnity_Excavator_small.unity
---------------------------------
This scene demonstrates an excavator (using the same controls as the one in the previous scene) that can dig in a small limited terrain area. 
The undercarriage is locked to the world.

When material leaves the bucket and collides with the large (blueish) box, the amount of digged material will be counted in volume and mass.

The shape of the terrain is computed procedurally in the script MassVolumeCounter.cs
The terrain can be reset with the key 'r'.