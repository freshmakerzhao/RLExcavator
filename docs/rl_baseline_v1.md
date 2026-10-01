# RL Follow 基线 v1

2026-10-01 冻结 `Assets/AIModels/ScaleFollow3cm_20260929.onnx` 为第一版 RL 基线。后续先以它跑通选点、挖掘和倾倒流程，再比较新的 RL 算法；不保留短路径模型作为另一份基线。

- 训练 run：`traj_full_follow_3cm_20260929`，最终 checkpoint：20,000,037 步。
- ONNX SHA-256：`8590762a1994213214e8a8a229b6feba4f72bc1cb3a7592d35f232dda7f8bce2`。
- 输入 `obs_0[batch,38]`，输出 `deterministic_continuous_actions[batch,4]`。
- 动作顺序 `[boom, stick, bucket, swing]`，输出是归一化速度参考；主项目按 shared profile 换算物理速度，再发给 Unity。
- 油缸伸长为正，行程分别为 boom `[-0.080,0.095]`、stick `[-0.090,0.060]`、bucket `[-0.045,0.085]` m；到点容差 0.03 m。

同目录的 `ScaleFollow3cm_20260929.baseline.json` 保存训练来源、模型/机器参数哈希、当时的机器参数与可达工作空间快照，以及验收摘要。这是冻结的实验记录，不是第二份运行配置。运行时仍只读取工作区 `shared/machine_profile.json`；改变方向、行程或观测定义后，需重新评估模型适配。

验收结果：两个新种子的已完成回合共 1047/1047 成功，MoveToDig 472 回合、CarryMaterial 575 回合；来自同一训练路点采样器，不含评估窗口结束时未完成的回合。ONNX 与 PyTorch 的 2558 个采样观测最大动作误差约 `9.54e-7`。主项目两组五路点和默认两路点均完成；停止后再保持 1 秒，尖端漂移约 0.059 mm。

该基线验证尖端跟踪与停止，未验证载荷保持、实际挖土、倾倒或真机部署。详细原始日志在工作区 `EvaluationReport/2026-10-01_rl_follow_acceptance.md`。

## 运行

Unity 打开 `Assets/Scenes/ExcavatorRuntime.unity` 并按 Play，然后执行：

```bash
conda activate earthmoving-autonomy
cd /home/zhaoshuai/workspace_uinty/RL_prj/earthmoving-autonomy
python -m earthmoving_autonomy follow
```

主项目 `config/follow.json` 默认使用本模型。日常只需修改路点与任务模式；不要用此前方向未修正的模型替换它。

## 仓库内容

Unity 保存运行场景、训练场景、模型及必要资产；主项目保存技能、观测构造、推理和设备通信。训练结果目录、构建产物、AGX 本机许可证、截图缓存与编辑器状态不进入 Git。官方卡车的实际依赖由 `Packages/manifest.json` 与 lock 固定到 bedtruck 1.1.1；完整示例导入仍保留在本机。
