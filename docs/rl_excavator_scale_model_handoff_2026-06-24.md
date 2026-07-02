# RLExcavator Scale Model Handoff

Generated: 2026-06-24 20:51 CST

## Scope

This handoff summarizes the latest discussion about the scale excavator model, real-machine deployment scripts, velocity-command policy design, FF+PI low-level control, and the decision to first run the full pipeline in simulation with the scale model.

Do not duplicate the earlier project handoff and system design. Read these first:

- `/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator/docs/rl_excavator_handoff_2026-06-22.md`
- `/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator/docs/HANDOFF.md`
- `/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator/docs/task_demo_system_design.md`

## Current Project Roots

- Unity project: `/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator`
- Scale-model deployment scripts: `/home/zhaoshuai/workspace_uinty/RL_prj/deploy_scale_model`

## Current Git / Workspace State

In `/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator`, current status at handoff time:

```text
 M Assets/Scenes/TestModel.unity
 M Assets/Scripts/Testing/ExcavatorActuatorPairTester.cs
 M ProjectSettings/Packages/com.algoryx.agxunity/EditorData.json
```

Notes:

- `Assets/Scripts/Testing/ExcavatorActuatorPairTester.cs` was updated to optionally log constraint state while holding a control key.
- `ProjectSettings/Packages/com.algoryx.agxunity/EditorData.json` appears to be Unity/AGX editor state; do not treat it as intentional unless verified.
- `Assets/Scenes/TestModel.unity` has user-side scale model setup changes; do not revert without explicit approval.

## Important Recent Commit

The last organized commit from this workstream:

```text
3c77cfb feat: add reachable workspace rrt demo
```

Remaining unrelated/untracked context from before may include `docs copy/` outside the committed scope.

## Current Consensus: Main Technical Route

The project uses route 1:

```text
RL policy outputs hydraulic-cylinder / joint target velocities.
The real machine low-level controller converts target velocities to joystick / valve / PWM commands.
```

The policy should not directly output joystick commands for the main route.

Core action definition:

```text
action = [swing_vel, boom_cyl_vel, arm_cyl_vel, bucket_cyl_vel]
range  = [-1, 1]
meaning = normalized target velocity commands
```

Unity execution:

```text
TargetSpeedController.Speed = action * maxSpeed
```

Real-machine execution goal:

```text
action * maxSpeed -> v_ref
v_ref -> FF + PI low-level velocity controller -> joystick/valve command u -> PWM
```

## Inference-Time System Order

The correct execution order is not "mapping first, sensors later". Sensor reading and state estimation are shared upstream inputs.

Recommended system order:

```text
Sensors
  -> State estimation / machine_state
  -> Perception mapping
  -> RRT* trajectory planning
  -> RL observation construction
  -> RL policy inference
  -> velocity scaling
  -> FF + PI velocity loop
  -> PWM / actuator interface with safety
  -> scale excavator execution
  -> feedback to sensors
```

Detailed module IO:

1. Sensor reading
   - Input: physical sensors.
   - Output:
     - cylinder displacement
     - cylinder velocity
     - swing angle
     - swing velocity
     - body / upper-structure pose
     - LiDAR data
     - image data

2. State estimation
   - Input: sensor stream.
   - Output: `machine_state`, including:
     - machine body pose
     - upper-structure swing angle
     - Boom / Arm / Bucket joint angles
     - Boom / Arm / Bucket cylinder velocities
     - bucket-tip pose
     - bucket-tip velocity
   - `machine_state` is a common input for mapping, planning, RL observation, and low-level velocity control.

3. Perception mapping
   - Input:
     - LiDAR data
     - image data
     - `machine_state`
   - Output:
     - `map`
     - obstacle information
     - traversable space

4. Trajectory planning
   - Input:
     - `machine_state`
     - `map`
     - target / dump point
     - reachable workspace
     - task mode
   - Output:
     - `RRT* waypoints`
   - Important: RRT* provides the route; RL knows the waypoints to track. RL is not expected to discover the path from raw perception.

5. RL observation construction
   - Input:
     - `machine_state`
     - `RRT* waypoints`
     - current target waypoint
     - tracking error
     - limit margins
     - task mode
   - Output:
     - `observation`

6. RL policy inference
   - Input: `observation`
   - Output: normalized target velocity action:
     - `[swing_vel_norm, boom_cyl_vel_norm, arm_cyl_vel_norm, bucket_cyl_vel_norm]`

7. Velocity conversion
   - Input: normalized action.
   - Output: real target velocities:

```text
v_ref = action * max_speed
```

8. FF + PI velocity closed-loop control
   - Input:
     - target velocity `v_ref`
     - measured velocity `v_meas` from `machine_state`
   - Error:

```text
e = v_ref - v_meas
```

   - Control law:

```text
u = FF(v_ref) + Kp * e + Ki * integral(e)
```

   - Meaning:
     - `FF(v_ref)`: feedforward joystick/valve estimate from real-machine calibration.
     - `Kp * e`: fast velocity-error correction.
     - `Ki * integral(e)`: compensation for load, friction, and hydraulic pressure variation.
   - Output:
     - joystick / valve opening command `u`

9. PWM output and safety protection
   - Input:
     - `u`
     - limit state
     - communication state
     - emergency-stop state
   - Output:
     - PWM / servo angle / motor command
   - Must include:
     - command clamp
     - dead-zone compensation
     - rate limiting
     - anti-windup
     - mechanical limit protection
     - timeout-to-zero
     - emergency stop

## Current Real-Machine Deploy Scripts

Path:

```text
/home/zhaoshuai/workspace_uinty/RL_prj/deploy_scale_model
```

Files:

- `doublestick_send.py`: joystick sender from the user's local control device.
- `excavator.py`: ORIN-side UDP receiver and PCA9685 control.
- `rollout_action_send.py`: sends recorded testbed rollout actions into the same UDP receiver.

Current deploy scripts do not yet accept target velocity directly. They accept joystick-like axis values and convert them to servo/motor commands.

Current action order in `rollout_action_send.py`:

```text
ACTION_ORDER = ("swing", "boom", "stick", "bucket")
```

Current mapping:

```text
X1 -> swing
Y2 -> boom
Y1 -> stick
X2 -> bucket
Z1/Z2 -> track motors, kept zero for rollout playback
```

Current ORIN-side control in `excavator.py`:

```text
swing:  X1 * 20 + 5      -> CHANNEL_4 motor
arm:    Y1 * 45 + 90     -> CHANNEL_3 servo
bucket: X2 * 45 + 90     -> CHANNEL_1 servo
boom:   Y2 * 45 + 90     -> CHANNEL_2 servo
pump:   -abs(axis) * 30  -> CHANNEL_5 motor
tracks: -Z1/Z2 * 20 + 5  -> CHANNEL_7/8 motors
```

Implication:

- Before real-machine velocity control exists, real-machine calibration must measure `joystick axis u -> measured cylinder velocity v_meas`.
- Later, use this calibration to build `FF(v_ref)` for the low-level velocity controller.

## Scale Model Sensors and Feedback

The real scale model has cable/pull-wire sensors installed. These can measure:

- hydraulic cylinder displacement accurately
- instantaneous cylinder velocity

From these, joint angles and joint velocities can be derived.

This is sufficient for route 1:

```text
policy target cylinder velocity -> FF + PI -> joystick/valve/PWM
measured cylinder velocity closes the low-level loop
```

First low-level controller should be built directly on cylinder velocity feedback. Joint angle and bucket-tip state are primarily used for observation, trajectory tracking, reachable workspace checks, and safety.

## Current Decision: No Real-Machine Test Yet

The user currently cannot perform real-machine tests. Decision:

1. First run the full pipeline in simulation using the scale excavator model.
2. Do not over-tune "realistic feel" without real data.
3. Keep the interfaces aligned with the future real-machine route.

This is not considered wasted work because the following remain valid later:

- observation/action definitions
- RRT* planner interface
- waypoint tracking policy interface
- task/demo state machine
- ONNX inference flow
- scale model AGX control wiring

Avoid:

- training the policy to output raw joystick commands
- using observations that the real machine cannot provide
- hard-coding AGX-only semantics into policy-level interfaces
- excessive subjective tuning before real-machine calibration

## Current Simulation Goal

Next practical target:

```text
Use the imported scale model in Unity to run the complete planning + RL tracking pipeline in simulation.
```

Minimum simulation requirements:

- scale model responds to four-dimensional velocity action:
  - `[swing, boom_cyl, arm_cyl, bucket_cyl]`
- AGX constraints and TargetSpeedControllers are correctly bound
- ranges/limits are plausible and reliable
- RRT* waypoints are generated within reachable workspace
- RL policy receives waypoints and machine state
- the model can execute "generate trajectory -> track trajectory -> complete/fail"

Use conservative parameter values until real-machine calibration exists:

- Range: use measured AGX scale model ranges where available.
- maxSpeed: reasonable engineering values, e.g. roughly `0.03~0.08 m/s` for cylinders until real measurements.
- ForceRange: enough to move, not so high that limits are violently hit or unrelated joints are dragged.
- Damping/Friction: stable, not oscillatory, not visibly drifting.

## Measured / Suggested AGX Ranges From Recent Test

The user measured rough constraint positions using the tester logger.

Suggested initial ranges:

```text
Boom_R   Range = -0.095  ~  0.080
Arm_R    Range = -0.060  ~  0.090
Bucket_R Range = -0.065  ~  0.045
```

Original measured approximate endpoints:

```text
Boom_R   -0.1038 ~ 0.0893
Arm_R    -0.0708 ~ 0.1022
Bucket_R -0.0767 ~ 0.0526
```

Important:

- `Bucket_R rangeEnabled` was false in the test and had to be enabled.
- Measure left-side actuators separately if they are driven too; do not assume mirrored constraints share exact sign/zero.

## Recent Script Change: Constraint Tester Logging

File:

```text
/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator/Assets/Scripts/Testing/ExcavatorActuatorPairTester.cs
```

Added fields:

```text
logConstraintState
stateLogInterval
```

When enabled and a bound key is held, the tester logs:

```text
[ConstraintTester] <name> pos=<current> range=(min,max) rangeEnabled=<bool> rangeForce=<force> motorSpeed=<speed> motorForce=<force>
```

Purpose:

- read `Constraint.GetCurrentAngle()` during manual actuator tests
- determine practical AGX range endpoints
- diagnose whether Range is enabled and whether Range/TargetSpeed controllers are exerting force

## Paper/System Diagram Prompt Context

The user is preparing a paper-style framework figure. A concise intended figure structure:

- Left: multi-source observations
  - perception inputs
  - proprioception
  - state estimation / machine_state
- Middle: planning and RL tracking
  - mapping and workspace representation
  - RRT* trajectory planner
  - RL trajectory tracking policy
- Right: closed-loop low-level execution
  - velocity scaling
  - FF + PI velocity controller
  - PWM and actuator interface
  - scale excavator
- Bottom dashed feedback:
  - state feedback
  - updated environment observations

Key statement:

```text
Perception mapping provides environment information, RRT* generates executable waypoints, RL policy outputs target velocities, and FF+PI low-level control converts target velocities into real-machine joystick / valve / PWM commands.
```

## Recommended Next Steps

1. In Unity, focus on `TestModel` and the imported scale excavator.
2. Bind the scale model constraints to the existing control abstraction or a small adapter compatible with `ExcavatorController` semantics.
3. Verify single-actuator control:
   - boom
   - arm
   - bucket
   - swing
4. Verify paired actuators where applicable:
   - left/right cylinders should receive the same semantic command but may have inverted signs.
5. Connect the scale model to the existing trajectory tracking pipeline.
6. Run a simple fixed waypoint path before RRT*.
7. Then run RRT* generated waypoints in simulation.
8. Only after the full simulation pipeline works, return to real-machine calibration and `deploy_scale_model` refactoring.

## Suggested Skills

- `diagnose`: Use for Unity/AGX control bugs, Range/TargetSpeed behavior, or policy tracking failures.
- `handoff`: Use when compacting another long session.
- `review`: Use before committing scale-model integration changes.
- `zoom-out`: Use if system boundaries between perception, planning, RL, and low-level control get blurry again.

## Important Cautions

- Do not revert user scene/model changes without asking.
- Do not assume Cat365 AGX parameters fit the scale model; use scale-model measured ranges and later real-machine calibration.
- Do not switch the main route to direct joystick-output policy unless the user explicitly changes the research direction.
- Do not conflate the current `deploy_scale_model` joystick-axis interface with the desired future target-velocity policy interface. The missing bridge is the low-level FF+PI velocity controller.

