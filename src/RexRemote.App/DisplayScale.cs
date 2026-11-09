using System.Text.Json.Serialization;

namespace RexRemote.App;

/// <summary>リモート画面の表示倍率。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayScale>))]
public enum DisplayScale
{
    /// <summary>縦横比を保ってウィンドウに合わせる（WPF が拡大縮小）。</summary>
    Fit,

    /// <summary>
    /// 50%（Retina 等倍）。App 側で 2x2 平均の 1/2 縮小をし、1 ピクセル = Windows の 1 デバイスピクセルで表示する。
    /// 3840x2160 の Mac が「見た目の解像度」1920x1080 でくっきり表示される。
    /// </summary>
    Half,

    /// <summary>100%（ドット等倍）。1 リモートピクセル = 1 デバイスピクセル。</summary>
    Full,
}
