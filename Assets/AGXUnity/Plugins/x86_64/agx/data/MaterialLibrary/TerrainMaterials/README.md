This document describes the format for TerrainMaterial specification files.
The basic structure is in .json format that the reflects the underlying class layout
of the TerrainMaterial class.

All parameters are in SI units and below follows a sample configuration file where
all the parameters have their units explained.

The top-level `name` field is optional and should contain a short identifier for
the material. If a file-based preset omits `name`, the JSON file name without the
extension is used as fallback. The `description` field can contain a longer
explanation.

The `TerramechanicsProperties` block is optional. If it is omitted, TerrainWheel
interactions use the default values from `TerrainMaterial::TerramechanicsProperties`.

Note: The unit comments below exists for educational purposes, the JSON format
      does not support comments. They either have to be removed if this sample should
      be used. It is instead recommended to use one of the sample preset files as a base for usage.

This is a sample file for the "dirt_1" preset, augmented with unit comments.
```
{
   "BulkProperties" : {
      "cohesion" : 12000.0,                  // Pa
      "density" : 1300.0,                    // kg/m3
      "dilatancyAngle" : 0.2268928027592629, // Radians
      "frictionAngle" : 0.7504915783575616,  // Radians
      "maximumDensity" : 2000.0,             // kg/m3
      "poissonsRatio" : 0.150,               // Dimensionless
      "swellFactor" : 1.280,                 // Dimensionless
      "youngsModulus" : 5000000.0,           // Pa
      "deltaReposeAngle" : 0.15              // Radians
   },
   "CompactionProperties" : {
      "angleOfReposeCompactionRate" : 24.0,        // Dimensionless
      "bankStatePhi" : 0.6666666666666666,         // Dimensionless
      "compactionTimeRelaxationConstant" : 0.050,  // Dimensionless
      "compressionIndex" : 0.110,                  // Dimensionless
      "hardeningConstantKE" : 1.0,                 // Dimensionless
      "hardeningConstantNE" : 0.08333333333333333, // Dimensionless
      "preconsolidationStress" : 98000.0,          // Pa
      "stressCutOffFraction" : 0.010,              // Dimensionless
      "dilatancyAngleScalingFactor" : 0.261799     // Radians
   },
   "ExcavationContactProperties" : {
      "aggregateStiffnessMultiplier" : 0.050,       // Dimensionless
      "depthDecayFactor" : 2.0,                     // Dimensionless
      "depthIncreaseFactor" : 1.0,                  // Dimensionless
      "excavationStiffnessMultiplier" : 1.0,        // Dimensionless
      "maximumAggregateNormalForce" : "inf",        // N
      "maximumContactDepth" : 1.0                   // m
   },
   "ParticleProperties" : {
      "adhesionOverlapFactor" : 0.050,              // Dimensionless
      "particleCohesion" : 200.0,                   // Pa
      "particleFriction" : 0.40,                    // Dimensionless
      "particleRestitution" : 0.0,                  // Dimensionless
      "particleRollingResistance" : 0.10,           // Dimensionless
      "particleTerrainCohesion" : 200.0,            // Pa
      "particleTerrainFriction" : 0.70,             // Dimensionless
      "particleTerrainRestitution" : 0.0,           // Dimensionless
      "particleTerrainRollingResistance" : 0.70,    // Dimensionless
      "particleTerrainYoungsModulus" : 100000000.0, // Pa
      "particleYoungsModulus" : 10000000.0          // Pa
   },
   "TerramechanicsProperties" : {
      "sinkageExponentParameterA" : 1.0,             // Dimensionless
      "sinkageExponentParameterB" : 0.0,             // Dimensionless
      "shearModulusTangentialParameterA" : 0.036,    // m
      "shearModulusTangentialParameterB" : 0.043,    // m
      "shearModulusLateralParameterA" : 0.013,       // m
      "shearModulusLateralParameterB" : 0.020,       // m
      "cohesiveModulusBekker" : 1000.0,              // N/m^(n+1)
      "frictionalModulusBekker" : 800000.0,          // N/m^(n+2)
      "cohesiveModulusReece" : 670.0,                // Dimensionless
      "frictionalModulusReece" : 200.0,              // Dimensionless
      "maximumNormalStressAngleParameterA" : 0.4,    // Dimensionless
      "maximumNormalStressAngleParameterB" : 0.15,   // Dimensionless
      "rearAngleParameterA" : -0.12,                 // Dimensionless
      "rearAngleParameterB" : 0.0                    // Dimensionless
   },
   "name" : "dirt_1",       // String, optional
   "description" : "DIRT_1" // String
}
```
