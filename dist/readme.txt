BveTs Structure Editor
======================

バージョン : 0.1.0
作者       : NT/fiv
配布ページ : https://github.com/ntfiv/BveTsStructureEditor
種別       : フリーソフト（利用規約.txt を必ずお読みください）


■ これは何？
  BVE Trainsim 用のストラクチャ（DirectX の .x ファイル）を作ったり直したりする
  Windows 用のツールです。
  ・.x（テキスト / バイナリ / 圧縮）と .pmx を開いて、3D で見ながら編集できます
  ・裏向きの面を赤で表示するので、BVE で描かれない面がすぐわかります
  ・箱・板・円柱・パイプ、描いて押し出す、回転体、インセット、面取り、配列複製など
  ・UV エディタ、テクスチャの歪み補正・シームレス化
  ・BVE 互換チェック、文字から看板、一括変換、Repeater 用の分割
  ・下絵（図面・写真・地理院地図）を敷いて、なぞって作れます


■ 動作環境
  ・Windows 10 / 11（64 bit）
  ・.NET 10 デスクトップ ランタイム（x64）
      入っていないと、起動したときにダウンロードを案内する画面が出ます。
      次のページの「.NET デスクトップ ランタイム 10.x」の「Windows x64」から入れてください。
      https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0


■ インストール
  zip を好きな場所に展開し、BveTsStructureEditor.exe を起動してください。
  インストーラーはありません。レジストリも使いません。

  ※ 初めて起動するとき、Windows の「PC は保護されました」(SmartScreen) が出ることがあります。
    その場合は「詳細情報」→「実行」で起動できます。


■ アンインストール
  展開したフォルダーを削除してください。
  設定も消す場合は、次のフォルダーも削除してください。
    %AppData%\BveTsStructureEditor        （画面の状態・最近のファイル・下絵の設定など）
    %LocalAppData%\BveTsStructureEditor   （地理院地図のキャッシュ）


■ 使い方
  同じフォルダーの manual.html（説明書）をブラウザで開いてください。
  アプリのメニュー「ヘルプ → 説明書を開く」（F1 キー）からも開けます。


■ 不具合の報告・要望
  配布ページの Issues へお願いします。
  https://github.com/ntfiv/BveTsStructureEditor/issues
  報告のときは、バージョン・操作の手順・（できれば）問題の出るファイルを添えてください。
  予期しないエラーが起きたときは、%TEMP%\BveTsStructureEditor_error.log に詳細が残ります。


■ 同梱ファイル
  BveTsStructureEditor.exe   本体
  *.dll ほか                 本体が使うファイル（消さないでください）
  manual.html                説明書
  readme.txt                 このファイル
  利用規約.txt               利用規約
  THIRD-PARTY-NOTICES.txt    使用しているライブラリのライセンス


■ 更新履歴
  0.1.0 (2026-10)  初公開
