# NuGet 发布配置指南

本文档说明如何配置和使用自动化发布 NuGet 包的功能，特别是针对 AOT 版本的 MessagePipe 包。

## 📦 AOT 版本包列表

以下包将发布为 AOT 兼容版本（在原有名称基础上添加 `.Aot` 后缀，并将 `MessagePipe` 前缀改为 `MyMessagePipe` 以避免与原始包冲突）：

| 原包名 | AOT 包名 | AOT 兼容性 | 说明 |
|--------|----------|------------|------|
| `MessagePipe` | `MyMessagePipe.Aot` | ✅ 完全支持 | 核心库，包含 Native AOT 支持 |
| `MessagePipe.SourceGenerator` | `MyMessagePipe.SourceGenerator.Aot` | ✅ 完全支持 | AOT 源生成器 |
| `MessagePipe.Interprocess` | `MyMessagePipe.Interprocess.Aot` | ✅ 完全支持 | 进程间通信扩展（依赖均为 AOT 兼容） |
| `MessagePipe.Analyzer` | `MyMessagePipe.Analyzer.Aot` | ✅ 完全支持 | Roslyn 分析器 |

**注意：以下包由于依赖项的 AOT 兼容性问题，暂不发布 AOT 版本：**

| 原包名 | 状态 | 原因 |
|--------|------|------|
| `MessagePipe.Redis` | ❌ 不发布 AOT 版本 | StackExchange.Redis v2.5.61 未声明 AOT 兼容，大量使用反射 |
| `MessagePipe.Nats` | ❌ 不发布 AOT 版本 | AlterNats v1.0.0 已停止维护，官方推荐使用 nats-io/nats.net.v2 |

如需在这些场景中使用 AOT，建议：
- **Redis**: 等待 StackExchange.Redis 官方宣布 AOT 支持，或考虑其他 Redis 客户端
- **NATS**: 迁移到官方的 [NATS.NET v2](https://github.com/nats-io/nats.net.v2) 客户端

## 🔑 配置发布密钥

### 方法一：使用 NuGet Trusted Publishers（推荐）⭐

NuGet Trusted Publishers 使用 OIDC（OpenID Connect）进行身份验证，无需存储 API 密钥，更安全。

#### 步骤：

1. **在 NuGet.org 注册组织**
   - 访问 https://www.nuget.org/
   - 登录并创建组织（如果还没有）

2. **配置 Trusted Publisher**
   - 访问 https://www.nuget.org/manage
   - 选择你的组织
   - 点击 "Trusted publishers" 标签
   - 点击 "Add trusted publisher"
   
3. **填写 GitHub Actions 信息**
   ```
   Owner: <your-github-username-or-org>
   Repository: <your-repository-name>
   Workflow name: publish-aot-nuget.yaml
   Environment: nuget-publish (optional but recommended)
   ```

4. **验证配置**
   - 保存后，NuGet 会显示一个验证字符串
   - 运行一次 workflow（dry-run 模式）
   - 在 NuGet 上确认验证

5. **在 GitHub Secrets 中添加用户（可选）**
   ```
   Settings → Secrets and variables → Actions
   New repository secret:
   Name: NUGET_USER
   Value: <your-nuget-username>
   ```

#### 优点：
- ✅ 无需存储长期有效的 API 密钥
- ✅ 自动过期，更安全
- ✅ 支持细粒度的权限控制
- ✅ NuGet 官方推荐方式

---

### 方法二：使用 API 密钥（传统方式）

如果无法使用 Trusted Publishers，可以使用传统的 API 密钥方式。

#### 步骤：

1. **获取 NuGet API 密钥**
   - 访问 https://www.nuget.org/account/apikeys
   - 点击 "Create"
   - 填写密钥名称（如：GitHub Actions AOT Publish）
   - 选择要发布的包（可以选择特定包前缀 `MyMessagePipe.*.Aot`）
   - 设置过期时间（建议设置）
   - 点击 "Create" 并复制生成的密钥

2. **在 GitHub 配置 Secret**
   - 进入仓库 Settings → Secrets and variables → Actions
   - 点击 "New repository secret"
   - 添加以下 secrets：
   
   ```
   Name: NUGET_API_KEY
   Value: <你刚才复制的 API 密钥>
   ```

   （可选）如果使用特定用户发布：
   ```
   Name: NUGET_USER
   Value: <你的 NuGet 用户名>
   ```

3. **验证配置**
   - 确保 secret 已保存
   - 运行 workflow 进行测试

#### 安全建议：
- ⚠️ 定期轮换 API 密钥（建议每 90 天）
- ⚠️ 设置合理的过期时间
- ⚠️ 限制密钥只能发布特定包
- ⚠️ 不要在代码中硬编码密钥

---

## 🚀 发布流程

### 1. 准备发布

确保：
- 所有代码已提交并推送到主分支
- 版本号已更新（通常在 csproj 或通过 workflow 输入）
- 测试已通过

### 2. 触发发布 Workflow

#### 通过 GitHub UI：

1. 进入仓库的 **Actions** 标签
2. 选择 **"publish-aot-nuget"** workflow
3. 点击 **"Run workflow"**
4. 填写参数：
   - **tag**: 版本号（例如：`2.3.0`）
   - **dry-run**: 
     - `true` = 仅构建和打包，不实际发布（用于测试）
     - `false` = 构建并发布到 NuGet

5. 点击 **"Run workflow"**

#### 通过 GitHub CLI：

```bash
# Dry run 模式（测试）
gh workflow run publish-aot-nuget.yaml \
  --field tag=2.3.0 \
  --field dry-run=true

# 正式发布
gh workflow run publish-aot-nuget.yaml \
  --field tag=2.3.0 \
  --field dry-run=false
```

### 3. 监控发布过程

- 在 Actions 页面查看 workflow 运行状态
- 检查每个 job 的日志输出
- 确认所有包都成功推送

### 4. 验证发布结果

发布完成后：

1. **检查 NuGet.org**
   - 访问 https://www.nuget.org/packages?q=MyMessagePipe.Aot
   - 确认新版本已出现

2. **本地验证**
   ```bash
   dotnet add package MyMessagePipe.Aot --version 2.3.0
   ```

3. **检查构建产物**
   - 在 workflow 页面下载 artifacts
   - 验证 `.nupkg` 和 `.symbols.nupkg` 文件

---

## 📝 故障排查

### 常见问题

#### 1. 认证失败

**OIDC 方式：**
```
error: The request was canceled due to an unhandled exception: 
The remote certificate is invalid according to the validation procedure.
```
- 检查 Trusted Publisher 配置是否正确
- 确认 workflow 名称匹配
- 验证 organization/repository 名称

**API Key 方式：**
```
error: Response status code does not indicate success: 401 (Unauthorized).
```
- 检查 `NUGET_API_KEY` secret 是否正确设置
- 确认 API 密钥未过期
- 验证密钥有权限发布目标包

#### 2. 包已存在

```
error: Package 'MyMessagePipe.Aot 2.3.0' already exists
```
- 版本号冲突，请使用新的版本号
- 或者删除 NuGet 上的现有版本（需要管理员权限）

#### 3. 依赖问题

```
error: Package dependency 'MyMessagePipe' could not be resolved
```
- 确保先发布基础包（`MyMessagePipe.Aot`）
- 再发布依赖包（如 `MyMessagePipe.Interprocess.Aot`）
- 考虑调整发布顺序或使用相同的版本号

#### 4. 构建失败

```
error CSxxxx: Compilation error
```
- 在本地运行 `dotnet build -c Release` 验证
- 检查所有项目引用是否正确
- 确认 .NET SDK 版本匹配

---

## 🔧 高级配置

### 自定义发布环境

可以在 workflow 中配置 environment protection rules：

1. **创建环境**
   - Settings → Environments → New environment
   - 名称：`nuget-publish`

2. **配置保护规则**
   - Required reviewers: 指定审批人
   - Deployment branches: 限制可发布的分支
   - Wait timer: 设置等待时间

3. **在 workflow 中引用**
   ```yaml
   environment:
     name: nuget-publish
     url: https://www.nuget.org/packages?q=MyMessagePipe.Aot
   ```

### 批量发布脚本

本地测试发布：

```bash
#!/bin/bash
VERSION="2.3.0"

# Build
dotnet build -c Release -p:Version=$VERSION

# Pack
dotnet pack -c Release --no-build -p:Version=$VERSION -p:IncludeSymbols=true -o ./publish

# Push (dry run)
for pkg in ./publish/*.nupkg; do
  echo "Would push: $pkg"
done

# Push (actual)
# for pkg in ./publish/*.nupkg; do
#   dotnet nuget push "$pkg" -s https://api.nuget.org/v3/index.json -k $NUGET_API_KEY
# done
```

### 条件发布

只在特定条件下发布：

```yaml
# 只在 tag 推送时发布
on:
  push:
    tags:
      - 'v*.*.*'

# 或只在主分支
on:
  push:
    branches: [ main ]
```

---

## 📊 发布清单

每次发布前检查：

- [ ] 所有单元测试通过
- [ ] AOT 兼容性测试通过
- [ ] 版本号正确更新
- [ ] CHANGELOG 已更新
- [ ] README 文档已更新
- [ ] Git 标签已创建
- [ ] NuGet API 密钥有效（如使用）
- [ ] Trusted Publisher 配置正确（如使用）
- [ ] 先执行 dry-run 测试

---

## 🔗 相关链接

- [NuGet Trusted Publishers 文档](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishers)
- [GitHub Actions 文档](https://docs.github.com/en/actions)
- [NuGet 包创建和发布](https://learn.microsoft.com/en-us/nuget/quickstart/create-and-publish-a-package-using-the-dotnet-cli)
- [Native AOT 部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

---

## 📞 支持

如有问题，请：
1. 查看本仓库的 Issues
2. 查阅 GitHub Actions 日志
3. 联系维护者
