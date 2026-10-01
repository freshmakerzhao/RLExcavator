# 旧算法验证演示（已退役）

`ExcavatorAlgorithmValidationScene.unity` 和原打包入口已随场景整理移除。旧实现可从 Git 历史查看，不能作为当前构建或基线测试入口。

当前运行场景为 `Assets/Scenes/ExcavatorRuntime.unity`；Unity 负责物理执行，Follow/ACT 策略在主项目运行。操作方式见 [运行场景说明](excavator_runtime_v1.md) 和 [RL 基线说明](rl_baseline_v1.md)。训练播放器入口见 [训练环境说明](mlagents_training_environment.md)。

`E4HistoricalReplayBuild` 仅用于历史实验回放，需要先恢复它对应的旧场景，不属于当前 Follow 基线。
