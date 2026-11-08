# 随课 · CoursePilot-plus

Windows 11 桌面课程自动化工具。通过本地截图、Windows OCR、鼠标与键盘操作识别课程目录和页面，并提供 DeepSeek / 智谱 GLM 双模型测验审核。

当前版本：**v0.6.12** · C# / WPF · .NET Framework 4.8 · x64

## 下载与使用

从 [Releases](https://github.com/qiqizaoyou/CoursePilot-plus/releases) 下载便携 ZIP，解压后运行 `随课.exe`。

发布包及 SHA256 校验文件同时保存在 [releases/v0.6.12](releases/v0.6.12)。

1. 打开课程目录页，在程序中选择目标屏幕，并校准目录、课程页和测验区域。
2. 测验功能需要在设置中填写自己的 DeepSeek 与智谱 GLM API Key；视频流程使用已保存的点击区域。
3. 确认目标页面，启动任务。运行时保持目标页面可见，随时按 **F8** 停止。
4. 仲裁未通过时可人工填写答案，再通过程序的人工确认按钮继续。

详细操作和版本记录见 [使用说明](使用说明.txt)。识别结果受页面布局、显示缩放及遮挡影响；首次运行请核对校准区域和运行记录。

## 主要功能

- 识别目录完成状态与任务数量，处理视频任务并循环寻找后续课程。
- 支持单选、多选、判断和多空填空；进行交叉审核及 12 票仲裁，支持经确认的同义表达合并。
- 仲裁暂停后允许人工处理，恢复时保留人工答案。
- 自动提交测验，遇到未完成提示时执行一次漏题补查。
- 填空写入后和最终复核时复制实际文字逐字核对，保留下划线、符号、空白及大小写；实际文字不符最多纠正一次。
- 本地诊断日志与失败现场预览。

目录中数字大于 1 的任务按当前规则仅在本地跳过，不会修改平台的完成记录。

## 从源码构建

需要 Windows 11 x64、Windows PowerShell 5.1、.NET Framework 4.8，以及 Windows OCR 所需的语言支持。项目直接调用系统自带 C# 编译器，无需 NuGet 包。

在项目根目录执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `dist\v0.6.12\`。源码和构建脚本使用 UTF-8；Windows PowerShell 脚本保留 BOM。

## 离线验证

内置自测使用模拟点击、键盘、剪贴板及模型接口。执行：

```powershell
$test = Start-Process -FilePath '.\dist\v0.6.12\随课.exe' `
  -ArgumentList @('--self-test', 'artifacts\self-test') `
  -WindowStyle Hidden -Wait -PassThru
Get-Content '.\artifacts\self-test\test-results.txt'
exit $test.ExitCode
```

v0.6.12 发布前通过了 285 项针对性离线检查，涵盖填空真实文字核对、剪贴板恢复、跨页填空、漏题补查、选择题和人工继续；部分原图 OCR 回归使用未公开的本地证据图片，公开仓库不包含这些私人截图。该数字不是上面自测命令的固定结果数，也不代表已完成真实课程端到端验证。

## 数据与配置

- API Key 由用户自行提供，使用 Windows DPAPI 按当前用户加密保存；仓库不含密钥。
- 启用模型答题时，题目文本或截图会发往对应模型服务，可能产生 API 费用。
- 截图、区域配置和运行记录存放在程序旁的 `data` 目录；请勿将自己的运行数据提交到公开仓库。
- 填空回读会临时使用剪贴板，程序会备份并尝试恢复原内容；无法备份、复制或恢复时暂停。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/` | 程序、识别逻辑、模型接口及离线测试 |
| `assets/` | 程序图标 |
| `build.ps1` | Windows 构建脚本 |
| `releases/v0.6.12/` | 已验证的便携包、校验值及发行说明 |
| `使用说明.txt` | 完整使用说明与版本记录 |

## 许可证

本项目使用 [MIT License](LICENSE)。
