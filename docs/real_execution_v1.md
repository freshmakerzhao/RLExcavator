# RealExecutionV1：无头训练与固定案例评测

用途：在独立 Unity 场景中研究执行约束适配。保持原 47 维观测、四轴动作顺序、任务模式和奖励；使用空斗条件执行代理 `CONDITIONAL_LINEAR_PROXY`，不代表真实液压响应已经标定。

当前已完成批次与结果：[2026-11-05 实验记录](../../EvaluationReport/2026-11-05_rl_headless_execution_v1/README.md)。该批已结束，保留集已使用；下面的运行命令应使用新的输出目录，不覆盖或重新分配原批任务。

## 环境与外部资源

- 已验证平台：Ubuntu、Unity `6000.3.16f1`、AGXUnity、Linux Build Support；构建产物是 Linux `.x86_64` player。Windows 拉取代码不等于可以直接运行这些 player，也不影响另一个 F407 项目的 Windows 烧录。
- Python 使用已安装 ML-Agents 的 `ml_agents` 环境。此版本的本地 ML-Agents 源 commit 为 `a2777719560e4676be99e2ea128c5eb1fbeb3dbb`，Python 包版本 `1.2.0.dev0`，Unity 包 `4.0.3`。现有 requirements 的 editable 路径是原机器路径，异机安装时应指向自己的同版本 ML-Agents checkout；不要照抄 `/home/...` 路径。
- Unity 构建仍读取同级 `../shared/machine_profile.jsonc` 及它引用的 `../shared/reachable_workspaces/scale_excavator_workspace.json`。二者是外部权威资源，不能另造一份不同数值的副本。构建器将它们放入当前 player 的 `StreamingAssets`。
- 原策略评测需要 `results/continuous_path_v5_20261021_train/ExcavatorContinuousPath/ExcavatorContinuousPath-3000018.pt`。续训初始化还需要该 behavior 目录的 `checkpoint.pt`。`results/` 不在代码 Git 中，模型应单独交付并核对批次身份；ONNX 不能代替训练 PT。
- 本地工具仅依赖本仓库内的 runner、fixed-case evaluator 和严格 47D loader；不动态导入 `EvaluationReport` 中的旧脚本。分析绘图另需 matplotlib。

## 命令

以下从 `RLExcavator/` 根执行，`BATCH` 指向工作区 `EvaluationReport/` 中新建批次。配置在 `config/real_execution_v1_ppo.yaml`；训练/开发/保留 case 的权威内容在 `Assets/TrainingData/RealExecutionCasesV1.json`。

```bash
conda activate ml_agents
export BATCH="../EvaluationReport/<new_batch>"
```

| 目的 | 命令/入口 |
|---|---|
| 查看入口，不运行仿真 | `python -B tools/real_execution_v1/run_experiment.py --help` |
| 无仿真离线测试 | `python -B -m unittest discover -s tools/tests -p 'test_real_execution*.py' -v` |
| 重新构建已提交的训练与评测场景 | Unity `-executeMethod RLExcavator.Editor.RealExecutionTrainingBuild.RebuildLearningPlayers` |
| 构建采样场景 | Unity `-executeMethod RLExcavator.Editor.RealExecutionTrainingBuild.BuildSamplerLinux64` |

冻结 case 分组，仅写新目录，不启动 player：

```bash
python -B tools/real_execution_v1/run_experiment.py prepare \
  --source Assets/TrainingData/RealExecutionCasesV1.json \
  --output "$BATCH/cases"
```

固定开发案例评测，**此命令会启动无学习 Unity player**：

```bash
python -B tools/real_execution_v1/run_experiment.py benchmark \
  --cases "$BATCH/cases/dev.json" \
  --player Builds/ExcavatorContinuousPathRealV1Benchmark/ExcavatorContinuousPath.x86_64 \
  --checkpoint results/continuous_path_v5_20261021_train/ExcavatorContinuousPath/ExcavatorContinuousPath-3000018.pt \
  --model-name original3m --split dev --base-port 6181 \
  --output "$BATCH/original3m_dev"
```

更换 checkpoint 时使用另一全新输出目录及 model-name。`--controller policy` 是默认值；`--controller joint-witness` 只用于明确标记的关节空间诊断，不能作为 RL 成功率。保留集需明确换成 `holdout.json` 与 `--split holdout`；不是每次调参后的重复筛选工具。

生成训练命令，**只打印 JSON/argv，不启动训练**：

```bash
python -B tools/real_execution_v1/run_experiment.py train-command \
  --player Builds/ExcavatorContinuousPathRealV1/ExcavatorContinuousPath.x86_64 \
  --run-id <new_run_id> --base-port 6180
```

打印出的 argv 使用当前 Python，调用已有 `tools/mlagents_train_no_onnx.py`；首次为 `--initialize-from=continuous_path_v5_20261021_train`，同一 run 的显式续跑才用 `--resume`，两者不同时使用。不要添加会覆盖 resume 语义的 `init_path`。

本仓库没有迁入本批专用的 `run_pilot.py` 自动监督入口。新训练必须另立批次、保存源码/player/profile/case 身份并安排监督。PPO 总预算 500k、周期保存间隔 50k；首个约50k保存可能编号 **49999**。暂停应选择首个正编号、非空、已稳定至少1秒的周期文件，向本批拥有的训练进程发送 SIGINT 并核对正常退出，不能等到文件名达到50000才停止。原批暂停缺陷与实际100207步的事实保留在原报告中，不复用其旧冻结/日志启动新训练。

读取已有两组完整固定案例，生成新的分析图和派生数据，不启动仿真：

```bash
python -B tools/real_execution_v1/analyze_pilot.py \
  --baseline "$BATCH/original3m_dev" \
  --pilot "$BATCH/candidate_dev" \
  --freeze "$BATCH/training_freeze.json" \
  --output "$BATCH/comparison"
```

`--freeze` 是显式的训练前身份清单；分析保留已有清单的路径/哈希语义，不改写原 manifest。源 player、case 与 trace 路径必须仍可读取；跨机器搬运历史批次时不能默默把旧绝对路径替换成当前文件。分析要求同一 split 的16个配对任务，保留失败与截尾，不用逐帧数扩充样本量。输出只写新的 `comparison.json`、CSV、PNG 和报告。

`Builds/`、`results/`、原始 trace、日志、冻结清单和派生图属于运行工件，保存到既有模型目录或 `EvaluationReport/`，不加入源码提交。原报告脚本与已完成批次数据保持原样。
