# 更新日志（UPDATE_LOG）

本文件记录本机仓库同步远端更新的历史，最新记录在最上方。仅本地维护，不推送远端。

## 2026-11-26 · v1.11.1

- 更新日期：2026-11-26
- 更新前提交：`d8d253b` fix: 配置迁移至用户目录并补齐高德 Key，修复地图中心与设置页坐标
- 更新后提交：`687a5ad` docs: 更新变更日志至 v1.11.1
- 版本：v1.11.1
- 更新方式：`git fetch origin main` + `git reset --hard origin/main`

### 主要变更摘要

1. 配置迁移到 `%LOCALAPPDATA%\MaterialManagementApp\AppSettings.json`，旧配置自动迁移；
2. 高德 Key 补合并，`settings.key-merged` 防回填；
3. 项目部坐标任一为 0 视为无效，仓库不参与地图中心；
4. 经纬度改为单输入框，支持多分隔符、方向字母、度符号，失焦即时校验，底层仍拆四字段；
5. 安装包不再携带配置模板，快捷方式与显示名中文；
6. 版本号 1.11.1，数据库结构与 AppId 未变。

### 安装包信息

- 文件名：`MaterialManagementApp_Setup_1.11.1.exe`
- 大小：81,610,394 字节（77.8 MB）
- SHA256：`46C04C34E735D42B8308D7F266493C99EB22FE43715032FEF8204953A246E801`
- 本机路径：`MaterialManagementApp\installer\output\MaterialManagementApp_Setup_1.11.1.exe`
