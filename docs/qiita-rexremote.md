---
title: 出先の Windows から Mac mini を操作するため、シンプルな VNC クライアント「rexRemote」を作った
tags:
  - VNC
  - macOS
  - C#
  - WPF
  - tailscale
private: false
updated_at: ''
id: 15867acecbd8e53724c6
organization_url_name: null
slide: false
ignorePublish: false
---

## はじめに

kintone プラグインの開発環境を、Surface Laptop（Windows）から Mac mini に移しました。

普段の開発は、Surface Laptop の VS Code から **Remote-SSH** で Mac mini につないで行っています。Tailscale を入れておけば自宅でも出先でも同じようにつながり、エディターもターミナルもほぼ手元と同じ感覚で使えます。

ただ、開発していると **たまに Mac の画面そのものを操作したい場面** があります。ブラウザーで動作を確認したい、ダイアログの許可ボタンを押したい、Mac 側のアプリの設定を変えたい、といった場面です。

macOS には VNC 互換の「画面共有」が標準で入っているので、Windows から VNC で入れます。ただ、汎用の VNC ビューアは Mac 特有の点（macOS のユーザー名とパスワードによる認証、Command キー、Retina の 4K 画面）でひと手間かかります。そこで、**Mac につなぐことだけに絞ったシンプルな VNC クライアント「rexRemote」** を作りました。

https://github.com/rex0220/rexRemote

![image.png](https://qiita-image-store.s3.ap-northeast-1.amazonaws.com/0/100572/e52732c2-6d84-4c21-a6e5-d9dfc5e41036.png)

## rexRemote でできること

- **macOS のユーザー名とパスワードで接続**（Apple Remote Desktop 認証）。パスワードは Windows 資格情報マネージャーに保存できます
- **Win キー = Command、Alt キー = Option**。Win+C / Win+V で Mac の中のコピー・貼り付け、Win+Tab でアプリ切り替えができます。Ctrl と Command の入れ替えもできます
- **Retina の 4K 画面を見やすく表示**：「ウィンドウに合わせる」「50%（Retina 等倍）」「100%」から選べます
- マウスカーソルを手元で描くので、カーソルは画面の更新を待たずに動きます
- Mac のロック画面にパスワードを入力するボタン、元の解像度でのスクリーンショット、接続先の履歴、切断時の自動再接続

なお、**Windows と Mac の間のクリップボード共有には対応していません**（理由は後述）。

Windows 10 / 11 で動く WPF アプリです（.NET 10）。アプリ本体は外部パッケージを使わず、.NET の標準ライブラリだけで書いています。

## 使い方

### 1. Mac 側の準備

「システム設定 → 一般 → 共有」で **画面共有** をオンにします。あわせて、画面共有の「アクセスを許可」で、接続に使うユーザーが許可されていること（「すべてのユーザー」か、一覧に入っていること）を確認します。

### 2. ダウンロードして起動

[Releases](https://github.com/rex0220/rexRemote/releases) から zip をダウンロードし、展開した `RexRemote.exe` を起動します。インストールは不要です。

| ファイル | 内容 |
|---|---|
| `rexRemote-v0.1.0-win-x64.zip` | .NET ランタイム同梱。そのまま動く（約 57 MB） |
| `rexRemote-v0.1.0-win-x64-framework-dependent.zip` | .NET 10 デスクトップランタイムが入っている PC 向け（約 160 KB） |

:::note warn
コード署名をしていないので、初回起動時に Windows SmartScreen の警告が出ることがあります（「詳細情報」→「実行」）。
:::

### 3. 接続

ホストに Mac の名前、ポートに `5900` を入れて「接続」を押し、macOS のユーザー名とパスワードを入力します。

Tailscale を使う場合は、Windows と Mac の両方を同じ tailnet に参加させておきます。MagicDNS を使っていれば、ホストは `mac-mini` のような短い名前で届きます。起動と同時に接続することもできます。

```
RexRemote.exe mac-mini
```

### キー操作のまとめ

| Windows のキー | Mac での動作 |
|---|---|
| Win | Command |
| Alt | Option |
| Ctrl+Space | 入力ソース（ABC ⇔ 日本語）の切り替え |
| Caps Lock / PrintScreen | Mac には送らず Windows 側で働く |

接続中は Win キーや Alt+Tab も Mac に送ります。Windows 側に戻るときは、マウスでタスクバーなどをクリックします。

## 作ってみて分かったこと

ここからは、実装していて分かったことのメモです。

### Apple Remote Desktop 認証は DH + MD5 + AES

手元の macOS 27 では、画面共有に接続するとサーバーは `RFB 003.889` という Apple 独自のバージョンを名乗り、セキュリティタイプとして `[30, 33, 36, 35]` を提示してきました。このうち `30` が Apple Remote Desktop 認証で、macOS のユーザー名とパスワードを使います。

手順はシンプルです。

1. サーバーから Diffie-Hellman のパラメーター（生成元・素数・サーバー公開鍵）を受け取る
2. 共有鍵を計算し、鍵長に左ゼロ詰めして **MD5** を取り、これを AES-128 の鍵にする
3. 128 バイトのブロック（ユーザー名 64 バイト + パスワード 64 バイト、それぞれ NUL で終え、残りは乱数）を **AES-128-ECB** で暗号化して送る

```csharp:ArdAuthenticator.cs（抜粋・一部簡略化）
var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
var serverPub = new BigInteger(serverPublicKey, isUnsigned: true, isBigEndian: true);

BigInteger privateKey = GeneratePrivateKey(p, fillRandom);
BigInteger clientPub = BigInteger.ModPow(g, privateKey, p);
BigInteger shared = BigInteger.ModPow(serverPub, privateKey, p);

byte[] aesKey = MD5.HashData(ToFixedLength(shared, keyLength)); // 鍵長に左ゼロ詰めしてから MD5
using var aes = Aes.Create();
aes.Key = aesKey;
byte[] ciphertext = aes.EncryptEcb(credentialBlock, PaddingMode.None);
```

手元の macOS 27 では、DH は **生成元 5、鍵長 512 バイト（4096bit）** でした。4096bit の `ModPow` は重そうに見えますが、秘密指数を 512bit にすれば 1 回あたり数十 ms で終わります。

### ZRLE の zlib ストリームは接続全体で 1 本

描画には ZRLE を使っています。ZRLE は矩形ごとに zlib で圧縮されたデータが届きますが、**zlib のストリームは接続全体で 1 本** で、矩形ごとにリセットされません（[RFC 6143 の 7.7.6 節](https://www.rfc-editor.org/rfc/rfc6143#section-7.7.6)にも、1 つの接続で zlib のストリームを 1 つ使うので、矩形は順番どおりにエンコード・デコードしなければならない、とあります）。そのため .NET の `ZLibStream` を 1 つだけ作り、届いた圧縮データを後ろに足していく入力ストリームを下に敷いて、読み続ける作りにしました。

### 4K の Retina 画面を 3:2 の Surface に出す

Mac mini には 4K モニター（見た目は 1920x1080）がつないであるので、VNC で届く画面は **3840x2160** です。これを Surface Laptop（2256x1504）の画面に合わせて縮めると、画面幅だけで計算しても約 0.59 倍です。縮小後のピクセルと元の画像のピクセルがきれいに対応しないため、文字がややにじみます。

そこで「50%（Retina 等倍）」の表示では、3840x2160 を **2x2 ピクセルの平均でちょうど 1920x1080 に縮小** し、縮小後の 1 ピクセルを Windows の物理的な 1 ピクセルに対応させて表示しています（WPF が Windows の表示スケールに合わせて拡大する分は打ち消しています）。更新された矩形を含むブロックだけを縮めるので、4K の 1 画面まるごとでもデバッグビルドで 20ms ほどです。

ただ、実際に使ってみると 50% は小さく、普段は「ウィンドウに合わせる」で使っています。くっきり見たいときだけ 50% に切り替える使い方です。

### カーソルは等倍の大きさで届く

Cursor 擬似エンコーディングを要求すると、Mac はカーソルを画面に描かず、形状だけを送ってきます。これを Windows のカーソルとして手元で描けば、カーソルは画面の更新を待たずに動きます。

最初は画面と同じ倍率で縮めていたところ、カーソルがとても小さくなりました。手元の環境では、Mac は **カーソルを Retina の 2 倍ではなく等倍（矢印で 23x22 ピクセル程度）** で送ってきていたためです。そこで、Windows の表示スケールで拡大して、Windows の普段のカーソルと同じくらいの大きさにしています。

### 低レベルキーボードフックは UI スレッドで動かさない

Win キーや Alt+Tab を Mac に送るため、低レベルキーボードフック（`WH_KEYBOARD_LL`）で、ウィンドウにフォーカスがあるときだけキーを横取りしています。

ここで一度失敗しました。フックを UI スレッドで動かしていたところ、**rexRemote を使っていないときでも、Windows の Alt+Tab が重くなった** のです。低レベルフックでは、Windows 全体のキー入力がコールバックの完了を待ちます。そのため、画面の更新で UI スレッドが忙しいと、すべてのキー入力が待たされていました。

[Microsoft のドキュメント](https://learn.microsoft.com/ja-jp/windows/win32/winmsg/lowlevelkeyboardproc)でも、低レベルフックは設定したスレッドで呼び出されるため、メッセージループが必要とされています。また、処理がタイムアウトすると、フックが通知なく解除される場合があります。

フックを **専用スレッドの自前のメッセージループ** に移し、フックからは WPF の要素に触らず、UI スレッドが更新するフラグだけを読む形にして解決しました。

### クリップボード共有は macOS の画面共有では使えなかった

VNC にはクリップボードをやり取りするメッセージ（ClientCutText / ServerCutText）があるので実装してみましたが、**macOS 27 の画面共有では両方向とも画面上のクリップボードに反映されませんでした**。

SSH 経由で `pbcopy` やスクリプトを使って橋渡しする方法も試しました。しかし、Mac の画面上でコピーしても、SSH から見えるクリップボードの変更回数（`NSPasteboard` の `changeCount`）が変わりませんでした。今回試した範囲では、**SSH から見えるクリップボードは、画面上のアプリが使うものとは別** のようです。SSH 経由の方法では画面側のクリップボードと共有できず、Mac 側に常駐プログラムを入れることも避けたかったので、今回は見送りました。

## セキュリティの注意

**rexRemote は、ARD 認証の後の VNC 通信そのものを暗号化しません**（暗号化しているのはログイン時の資格情報だけです）。画面やキー入力を守るため、出先から接続するときは Tailscale などの VPN か、SSH トンネルを経由してください。私は Tailscale 経由で使っています（Tailscale は端末間の通信を暗号化します）。

## おわりに

「普段は VS Code の Remote-SSH、Mac の画面が必要なときだけ rexRemote」という使い分けで、Mac mini への移行後も Surface Laptop から快適に開発できています。

開発は Claude Code と一緒に進めました。実機（Mac）で確かめながら、キーボードの割り当てやカーソルの大きさなど、細かいところを詰めていきました。

ソースコードは MIT License で公開しています。同じように Windows から Mac を操作したい方の参考になれば幸いです。

https://github.com/rex0220/rexRemote

※ Apple、Mac、macOS、Apple Remote Desktop は Apple Inc. の商標です。rexRemote は Apple Inc. とは関係ありません。
