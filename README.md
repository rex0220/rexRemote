# rexRemote

Windows から macOS の「画面共有」（VNC）に接続する、Mac 専用のリモートデスクトップクライアントです。

汎用の VNC ビューアではなく、Mac につなぐことに特化しています。macOS のユーザー名とパスワードによる Apple Remote Desktop 認証、Win キーを Command として送る機能、Retina の 4K 画面を見やすく表示する機能などを備えています。

> A Windows client for macOS Screen Sharing (VNC), built with .NET / WPF. Japanese UI.

## 主な機能

- **Apple Remote Desktop 認証**：macOS のユーザー名とパスワードで接続します（Diffie-Hellman + AES）。VNC パスワード認証にも対応しています。
- **高速な描画**：ZRLE・CopyRect・Raw の各エンコーディングと、DesktopSize（画面サイズの変更）に対応しています。
- **Mac 向けのキーボード**：Win キーは Command、Alt キーは Option として送ります。Ctrl と Command は入れ替えられます。Win キーや Alt+Tab も Mac に送ります。
- **Retina 対応の表示**：「ウィンドウに合わせる」「50%（Retina 等倍）」「100%」から選べます。マウスカーソルは手元で描くので、遅れません。
- **パスワードの保存**：Windows 資格情報マネージャーに保存できます。Mac のロック画面にパスワードを入力するボタンもあります。
- **便利機能**：接続先の履歴、元の解像度でのスクリーンショット、切断時の自動再接続を備えています。

## 動作環境

- Windows 10 / 11
- [.NET 10 デスクトップランタイム](https://dotnet.microsoft.com/download/dotnet/10.0)（ビルドするには .NET 10 SDK）
- 接続先の Mac で「画面共有」がオンになっていること（システム設定 → 一般 → 共有）
  - macOS 27 で動作を確認しています。

## ビルドと実行

```
dotnet build
dotnet test
dotnet run --project src/RexRemote.App
dotnet run --project src/RexRemote.App -- mac-mini        # 起動と同時に接続する
```

## 使い方

1. ホストに Mac の名前か IP アドレスを、ポートに通常は `5900` を入力して「接続」を押します。
2. macOS のユーザー名とパスワードを入力します。「パスワードを保存する」を選ぶと、次回から入力を省略できます。
3. リモート画面をクリックすると、キーボード入力が Mac に送られます。

| Windows のキー | Mac での動作 |
|---|---|
| Win | Command（Win+C / Win+V でコピー・貼り付け、Win+Tab でアプリ切り替え） |
| Alt | Option |
| Ctrl+Space | 入力ソース（ABC ⇔ 日本語）の切り替え（Mac の標準ショートカット） |
| Caps Lock / PrintScreen | Mac には送らず、Windows 側で働きます |

詳しい操作方法は、アプリのツールバーの「？」で表示されるヘルプを見てください。

コマンドラインでは、次の指定ができます。

```
RexRemote.exe [host[:port]]   接続先を指定して起動し、すぐに接続する
RexRemote.exe --help          使い方を表示する
RexRemote.exe --version       バージョンを表示する
```

## 制限事項

- **クリップボード共有は使えません。** macOS の画面共有サーバーは、VNC 標準のクリップボードのやり取りを画面上のクリップボードに反映しません（macOS 27 で確認）。
- 音声とファイル転送には対応していません。

## セキュリティ上の注意

- Apple Remote Desktop 認証で暗号化されるのは、ログイン時の資格情報だけです。**その後の画面やキー入力は暗号化されません。** インターネット越しに使うときは、Tailscale などの VPN か SSH トンネルを経由してください。
  ```
  ssh -L 5900:localhost:5900 <Mac>    # この場合は localhost:5900 に接続する
  ```
- 保存したパスワードは、Windows 資格情報マネージャーに `rexRemote:ホスト:ポート` の名前で保存されます。
- ログ（`%LOCALAPPDATA%\RexRemote\rexremote.log`）には、パスワードや入力した文字は記録しません。

## 構成

```
src/RexRemote.Protocol/        RFB プロトコル（認証・デコード・キー変換）。UI に依存しない .NET ライブラリ
src/RexRemote.App/             WPF アプリ（描画・入力・接続 UI）
tests/RexRemote.Protocol.Tests xUnit によるユニットテスト
tools/probe_rfb.py             Mac 上で RFB のハンドシェイクを調べるスクリプト
```

アプリ本体は外部の NuGet パッケージを使わず、.NET の標準ライブラリだけで実装しています。

## ライセンス

[MIT License](LICENSE) - Copyright (c) 2026 rex0220

Apple、Mac、macOS、Apple Remote Desktop は Apple Inc. の商標です。本ソフトウェアは Apple Inc. とは関係なく、Apple Inc. が提供・保証するものではありません。
