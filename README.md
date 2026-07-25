# LoL Companion v0.1.0-alpha.1

LoL Companion 是一個 Windows 桌面輔助工具。目前這個 pre-release 版本只包含最小可用的 Discord 配對與 session 狀態流程。

## 目前版本範圍

- `v0.1.0-alpha.1`
- 目前僅支援 Discord 配對與 2 小時 memory-only session
- 尚未包含完整的分析 UI、LCU 深度整合或自動化背景處理
- 目標平台為 Windows x64

## 使用方式

1. 在 Discord 內執行 `/pair` 取得一次性配對碼。
2. 開啟 LoL Companion。
3. 輸入配對碼與裝置名稱。
4. 按下「配對」，完成後可在視窗內檢視 session 狀態與到期時間。

## 安全提醒

- 目前為未簽章的 Windows 應用程式，首次執行時可能出現 SmartScreen 未知發行者警告。
- 此版本不會把 session token 寫入磁碟，session 僅保留於記憶體中。
- 若關閉程式或 session 到期，需重新配對。

## 內含內容

- Discord 配對流程
- Session 狀態顯示
- Windows x64 可攜式發行包

## 尚未包含

- 完整分析 UI
- 自動更新
- 後台常駐服務
- LCU 遊戲內資料抓取流程
- 報表分享與進階工作流
