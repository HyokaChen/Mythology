// 功能：把任意文本渲染成终端二维码（Unicode 半块字符）
//
// 背景：终端格子高宽比约 2:1，故把两行模块合成一行字符——33×33 的码变成 17 行 × 33 列，
// 视觉上正好是方的。任何终端都显示得出来（含 Windows Terminal），无图像协议依赖。
//
// 从 CloudLoginDialog 抽出（原先内联为 private static RenderQrCode），两处调用共用一份。

using System.Text;
using QRCoder;

namespace Tui.Controls;

/// <summary>终端二维码渲染。</summary>
internal static class TerminalQr
{
    /// <summary>
    /// 渲染为多行文本。<paramref name="text"/> 通常是 URL。
    /// </summary>
    /// <returns>生成失败（文本过长、编码异常）时返回 null——兜底文案由调用方决定。</returns>
    public static string? Render(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var moduleCount = data.ModuleMatrix.Count;

            const int quietZone = 1;
            var totalCols = moduleCount + quietZone * 2;
            var textRows = (moduleCount + quietZone * 2 + 1) / 2;
            var sb = new StringBuilder(textRows * (totalCols + 1));

            for (var row = 0; row < textRows; row++)
            {
                for (var col = 0; col < totalCols; col++)
                {
                    var top = IsDark(data, row * 2 - quietZone, col - quietZone);
                    var bottom = IsDark(data, row * 2 + 1 - quietZone, col - quietZone);

                    sb.Append(
                        (top, bottom) switch
                        {
                            (true, true) => '█',
                            (true, false) => '▀',
                            (false, true) => '▄',
                            (false, false) => ' ',
                        }
                    );
                }

                if (row < textRows - 1)
                    sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsDark(QRCodeData data, int row, int col)
    {
        if (row < 0 || row >= data.ModuleMatrix.Count)
            return false;
        if (col < 0 || col >= data.ModuleMatrix[row].Count)
            return false;
        return data.ModuleMatrix[row][col];
    }
}
