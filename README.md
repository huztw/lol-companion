# LoL Companion v0.1.0-alpha.7

LoL Companion 是一個 Windows 桌面資料橋接工具。完成 Discord 配對後，它會在本機等待 Discord 指令、連接 League Client 並把使用者選定對局的分析資料安全送回 Bot。

## 目前版本範圍

- `v0.1.0-alpha.7`
- 支援 Discord 配對與 2 小時 memory-only session
- 支援由 Discord `/analyze` 取得並顯示最近 20 場對戰
- 支援從手機或電腦 Discord 選取對局，再由 Companion 讀取本機資料並送出分析
- 支援 analysis schema 4、數字英雄 ID、結算裝備／增幅裝置資料與 timeline-v3 戰局影響分析
- 近期對戰資料未變時保留清單與選取狀態，減少定時刷新造成的閃爍
- 改善 League Client 暫時性錯誤提示，避免清除既有清單或分析結果
- 視窗標題顯示目前 Companion 版本
- 尚未包含自動更新、背景常駐服務或安裝程式
- 目標平台為 Windows x64

## 使用方式

1. 在 Discord 內執行 `/pair` 取得一次性配對碼。
2. 開啟 LoL Companion。
3. 輸入配對碼與裝置名稱。
4. 按下「配對」，完成後可在視窗內檢視 session 狀態與到期時間。
5. 啟動並登入 League Client，讓 LoL Companion 保持開啟並顯示「等待 Discord 指令」。
6. 在手機或電腦 Discord 執行 `/analyze`，依畫面選擇裝置與近期對局。
7. Companion 完成資料讀取後，分析進度與私人報告會回到 Discord；必要時可用 `/report` 重新讀取。

## 安全提醒

- 目前為未簽章的 Windows 應用程式，首次執行時可能出現 SmartScreen 未知發行者警告。
- 此版本不會把 session token 寫入磁碟，session 僅保留於記憶體中。
- 目前不包含自動更新機制。
- 若關閉程式或 session 到期，需重新配對。

## 內含內容

- Discord 配對流程
- Session 狀態顯示
- Discord 遠端控制狀態
- 按 Discord 指令讀取 League Client 近期 20 場
- 由 Discord 選取對局並送出分析報告
- Windows x64 可攜式發行包

## 尚未包含

- 自動更新
- 後台常駐服務
- 安裝程式
- 公開分享與進階工作流
