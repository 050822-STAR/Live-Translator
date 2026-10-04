# 安全说明

## 报告漏洞

如果发现安全问题（例如 API Key 可能泄露、设置文件可被篡改利用等），请**不要公开提交 Issue**，而是通过仓库的
[私下报告漏洞](https://github.com/STAR-0925/Live-Translator/security/advisories/new) 功能联系维护者。

请尽量说明问题、影响范围和复现步骤。

## 本程序如何处理敏感数据

- API Key 使用 Windows DPAPI 加密后保存在 `%APPDATA%\LiveTranslator\settings.json`，只有当前 Windows 账户能解密。
- 识别出的字幕文本只会发送给你在设置中选择的翻译服务。
- 翻译记录默认不保存；开启后保存在本机 `%APPDATA%\LiveTranslator\logs`。
- 程序不包含任何统计或遥测。
