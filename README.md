# Listening Subtitles · 听力字幕

**专门用于语音识别、实时字幕转录和字幕文件导出的 Windows 桌面工具。**

识别电脑正在播放的语音，在浮动窗口显示原文，并可翻译成你选择的目标语言，导出 SRT / WebVTT（CC）或 TXT。适合没有字幕的视频、录播课程、播客和听力练习。界面支持简体中文和 English；识别语言与翻译目标分别设置，默认英语 → 简体中文。

**A Windows desktop app dedicated to speech recognition, live subtitle transcription and caption export.**

Transcribe speech playing through your computer, show the original text in a floating window, optionally translate it into your chosen target language, and export SRT, WebVTT (CC) or TXT. Use it for videos without captions, recorded lessons, podcasts and listening practice. The interface supports Simplified Chinese and English. Interface language, spoken language and translation target are separate settings; the default is English speech → Simplified Chinese translation.

[English guide / 完整英文说明](README.en.md) · [下载 / Download for Windows](https://github.com/Desolate-Jix/listening-subtitles/releases/latest) · [源码 / Source](https://github.com/Desolate-Jix/listening-subtitles) · [MIT License](LICENSE)

## 功能

- Windows 10/11 x64；中英文界面，可置顶、调节字号、显示或隐藏译文。首次打开的欢迎介绍也同时提供中英文。
- 使用共享模式 WASAPI loopback 采集电脑输出声音，自动跟随默认耳机/扬声器切换，也可固定设备。
- OpenAI Realtime API 语音识别；可自动将完整语段交给 Responses API，翻译成所选目标语言。
- 识别语言可自动判断，也可指定单个语言代码或逗号分隔的混合语言；翻译目标支持简繁中文、英语、日语、韩语等常用语言。
- 历史保留原文、译文及译文语言，晚到的译文更新原段落；可按当前目标语言补译，成功后替换该段的旧语言译文，失败时保留原译文。
- 本机加密保存历史，重启可回看，不因超过 500 段而丢弃早期记录。
- SRT / VTT / TXT 共用语言模式选择，可导出双语（原文 + 所选目标语言）、仅原文或仅目标语言。
- **默认首条字幕从 `00:00:00.000` 开始**，历史列表、TXT、SRT、VTT 共用同一相对时间轴，保留原始段落间隔。
- 自带密钥设置界面和 Windows 当前用户 DPAPI 加密存储。

当前版本采集的是**电脑声音**，不打开麦克风；播放设备上的其他应用声音也会被采集。音频文件可以在播放器中播放后转录，目前没有直接导入音频/视频文件的批量转录入口。

## 快速使用

1. 下载发行版 ZIP，完整解压到自己的文件夹，运行 `ListeningSubtitles.exe`。不要只复制 EXE。
2. 打开“设置”，填写你自己的 **OpenAI API Key**。不需要其他应用或 ChatGPT 网页会话。
3. 按需要选择简体中文或 English 界面，默认简体中文。界面语言不决定识别什么语音，也不决定翻译成什么语言。
4. 设置语音识别的源语言：默认 `en`（英语），混合语言可填 `zh,en`，留空由模型自动识别。默认识别模型为 `gpt-live-transcribe`，模型可用性取决于你的 API 账户。
5. 选择翻译目标语言，默认简体中文；自动翻译默认打开，默认翻译模型为 `gpt-6-luna`，可在设置中修改。“显示译文”只控制浮动窗的显示，关闭它仍会翻译；要停止翻译请求，请关闭自动翻译。
6. 播放视频，点击“开始”。启动应用本身不采集、不连接 API。点击“停止”结束采集和当前网络工作。
7. 点“历史”回看、按当前目标语言补译、保存或导出。上方列表可以选择过去的记录。新一次“开始”会保存当前记录，然后创建新记录。

可选目标语言：简体中文 `zh-Hans`、繁体中文 `zh-Hant`、英语 `en`、日语 `ja`、韩语 `ko`、法语 `fr`、德语 `de`、西班牙语 `es`、葡萄牙语 `pt`、俄语 `ru`、阿拉伯语 `ar`、印地语 `hi`、意大利语 `it`。例如，源语言 `ja`、目标 `en` 表示日语语音 → 英文字幕；源语言留空、目标 `zh-Hant` 表示自动识别语音 → 繁体中文字幕。识别和翻译效果取决于输入内容与所选模型，不保证所有语言都有相同效果。

使用语音识别、自动翻译或历史中的补译功能会产生 OpenAI API 费用。应用不包含额度，不依赖 ChatGPT 订阅，也不在失败后自动重试或重新开启采集。

## 历史与目标语言

更改目标语言不会把已有译文自动改成新语言。历史会标明旧译文的实际语言，避免把旧中文或英文误认为当前目标。补译针对当前选择的目标语言：已匹配的译文会保留，缺失或属于其他语言的完整语段可重新翻译；只有成功的新译文才会替换旧译文。补译使用录制时的源语言设置；早期历史没有这一信息时按自动识别处理。

“双语”指原文与当前目标语言，并不固定为中英双语。导出时只使用匹配当前目标的译文；其他语言的旧译文仍保留在历史中，需要时先按新目标补译。仅目标语言模式会略过尚无匹配译文的段落，导出前会提示缺失译文。

## 时间轴与 CC 字幕

在“历史 → 导出字幕 / TXT”中选择格式：

| 格式 | 用途 | 时间示例 |
| --- | --- | --- |
| SRT | 支持外挂字幕的播放器、字幕编辑器 | `00:00:00,000 --> 00:00:02,250` |
| WebVTT | 网页视频的字幕轨 / CC | `00:00:00.000 --> 00:00:02.250` |
| TXT | 按所选语言模式回看原文或译文 | `[00:00:00.000]` |

- **“首条字幕从 0 开始”默认勾选**：扣除第一条完整字幕的起始时间，后续间隔和时长保持原样。取消勾选后，从实际开始采集声音的时刻计时，保留开头的静音。
- “整体偏移（秒）”同时作用于列表、TXT 和字幕导出。正值让字幕延后，负值提前，超出零点的部分会裁剪。
- 例：第一句话在视频 `02:10` 开始，保持归零并输入偏移 `130`，即可把第一条字幕放到 `00:02:10.000`。
- 记录列表的日期和文件名日期仅用于查找录制场次，不是字幕时间。
- 时间来自本地音频分段，不使用译文返回时间；是近似分段时间，不提供逐词对齐。播放器暂停、变速或跳播不会自动同步，需要重新录制或在字幕编辑器中调整。
- SRT/VTT 只导出完整句；TXT 的原文和双语模式还会保留标注为“未完成”的尾句。
- 导出的文件是 UTF-8。可把 SRT 与视频同名后载入播放器；VTT 需通过支持字幕轨的网站/播放器载入，不会自动给任意网站添加 CC 按钮。

## 保存和隐私

- 设置、加密密钥及加密历史保存在 `%LOCALAPPDATA%\ListeningSubtitles`；历史在其中的 `history` 文件夹。
- 原文和译文变化后约每秒保存一次，正常停止/退出时也保存。断电或强制结束可能丢失最后尚未保存的内容。加密文件只能由原 Windows 账户解密，跨机器保留请导出字幕/TXT。
- 点击“清空”只清空当前页面，已保存场次仍可回看。删除不需要的本机记录可以在退出应用后管理 `history` 文件夹。
- 采集期间音频发送至 OpenAI；翻译只发送相应转写文本，使用无状态请求并设置 `store: false`。应用不保存原始音频，没有遥测、开机启动或后台自动采集。
- Key 仅保存在本机并用于 OpenAI 请求；不会写入源码、导出字幕或 Git。应用没有内置服务端或共享 Key。
- TXT/SRT/VTT 导出是普通可读文件，请自行选择保存位置。

## 已知限制

连续语音约每 5 秒提交一段，停顿约 800 毫秒也会分段。译文还需等待翻译请求，不能保证零延迟。停止时未提交的尾句、未完成的翻译会取消；可在停止后补译已有完整句。翻译队列有上限，积压或失败会保留原文供之后补译。

字幕可能识别或翻译错误；它不是视频原有的官方字幕。独占全屏、受保护内容或播放器自选的其他输出设备可能影响浮窗显示或音频采集。历史单场上限约 64 MB，达到上限时请先导出并开始新记录。

从早期版本升级：旧版本只在内存中的记录无法由新进程接管。请先导出旧窗口的内容再退出。已有本机加密历史可以继续读取；旧版中文译文按简体中文识别，默认仍为英语 → 简体中文。本应用需要独立配置自己的 API Key。

## 开发与打包

依赖：Windows 10/11 x64、.NET 8 SDK、NuGet NAudio 2.4.0。发行版自带 .NET 和 WindowsDesktop 运行时。

```powershell
dotnet run --project ListeningSubtitles.csproj -c Release
dotnet run --project tests/ListeningSubtitles.Tests.csproj -c Release
dotnet run --project tests/Speech/SpeechTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
# 运行 artifacts/win-x64/ListeningSubtitles.exe
```

`--preview <临时目录>` 生成合成字幕窗预览后退出，不读取实际 Key，不采集声音或访问 API。应用测试可传入临时目录，生成合成窗口图和样例 SRT/VTT 供检查。

测试使用合成音频和模拟 HTTP/WebSocket，无需 Key，不调用付费 API。这些离线检查不代表真实视频的识别准确率、各语言翻译质量或网络延迟，也不代表已经逐一实测所有支持语言。

目录：`Audio` 采集，`Speech` 实时转写，`Core` 历史/翻译/字幕导出，`UI` 浮窗与设置，`tests` 离线测试。

## 许可与参考

本项目使用 MIT License。NAudio 及派生的 `SafeWasapiCapture` 保留原版权声明，许可见 [licenses/NAudio-2.4.0.txt](licenses/NAudio-2.4.0.txt)。

- [OpenAI Realtime transcription](https://developers.openai.com/api/docs/guides/realtime-transcription)
- [Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [W3C WebVTT](https://www.w3.org/TR/webvtt1/)
- [SRT format overview](https://www.loc.gov/preservation/digital/formats/fdd/fdd000569.shtml)
