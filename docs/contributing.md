# 分支与提交

`main` 是当前整合主线。协作者在功能分支开发，向 `main` 提交 Pull Request，由仓库所有者 `@freshmakerzhao` 审查并执行合并。

```bash
git switch main
git pull --ff-only origin main
git switch -c feat/your-topic
# 修改并完成相关验证后，明确选择要提交的文件。
git add <files>
git commit -m "feat: describe the change"
git push -u origin feat/your-topic
```

在 GitHub 创建 PR，base 选 `main`，说明修改目的、影响范围和验证结果。协作者保留仓库写权限，可以创建和推送功能分支；外部贡献者可通过 fork 提交 PR。

`main` 使用 GitHub Rulesets 限制更新，唯一允许更新的账号为 `freshmakerzhao`。其他人不能直接推送或自行合并 PR，即使 PR 已获得批准。`.github/CODEOWNERS` 指定仓库所有者负责审查；真正的权限限制由 GitHub 服务端规则执行。所有者仍可正常推送紧急修复；`main` 的强制推送和删除对所有人禁用。

合并后的功能分支可删除。仍含未合并提交的分支保留，不能只因创建时间较早而删除。`real2sim-scene-20260807` 当前保留为未整合的历史实验分支。

Unity 的 `Library/`、`Temp/`、`Logs/`、构建输出和训练结果由现有 `.gitignore` 排除。不要提交本机许可证、凭证或缓存；场景和资源改动保留对应 `.meta` 文件，并检查 Unity 实际生成的差异。
