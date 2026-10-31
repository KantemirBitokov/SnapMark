using System.Text;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Services;

namespace SnapMark.Platform.Common;

/// <summary>
/// Поиск по картинке в Google.
///
/// Официального API у Google Lens нет. LightShot для этого сначала загружает картинку на свой
/// сервер (prnt.sc) и отдаёт Google публичную ссылку — то есть скриншот уходит третьей стороне.
/// Мы делаем без посредников: пишем во временную папку маленькую HTML-страницу, которая
/// сама отправляет картинку POST-запросом прямо в Google Lens из браузера пользователя.
///
/// Это неофициальный адрес загрузки, Google может его поменять. Поэтому есть запасной режим
/// (переключается в настройках): картинка копируется в буфер, открываются Google Картинки — вставить Ctrl+V.
/// </summary>
public static class GoogleLens
{
    const string UploadUrl = "https://lens.google.com/v3/upload";

    public static async Task SearchAsync(byte[] png, ImageSearchMethod method)
    {
        if (method == ImageSearchMethod.ClipboardAndOpen)
        {
            await PlatformServices.Clipboard.CopyAsync(new ClipboardPayload { Png = png, Dib = ImageEncoder.ToDib(png) });
            ShellOpen.Open("https://images.google.com/");
            UI.Toast.Show(L.T("Поиск в Google"), L.T("Картинка в буфере обмена. Нажми на значок камеры и вставь её (Ctrl+V)."));
            return;
        }

        // Большие скриншоты отправляем в JPEG: быстрее и в пределах лимитов Lens.
        string mime = "image/png", ext = "png";
        byte[] data = png;
        if (png.Length > 3_000_000)
        {
            data = ImageEncoder.Encode(png, ImageFileFormat.Jpg, 90);
            mime = "image/jpeg";
            ext = "jpg";
        }

        var dir = Path.Combine(Path.GetTempPath(), "SnapMark");
        Directory.CreateDirectory(dir);
        var htmlPath = Path.Combine(dir, $"lens_{DateTime.Now:HHmmssfff}.html");

        var html = Template
            .Replace("{{URL}}", UploadUrl)
            .Replace("{{MIME}}", mime)
            .Replace("{{NAME}}", "snapmark." + ext)
            .Replace("{{SENDING}}", System.Net.WebUtility.HtmlEncode(L.T("Отправляю изображение в Google Lens…")))
            .Replace("{{B64}}", Convert.ToBase64String(data));
        await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);

        ShellOpen.Open(htmlPath);
        UI.Toast.Show(L.T("Поиск в Google"), L.T("Открываю Google Lens в браузере…"));
    }

    const string Template = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>SnapMark → Google Lens</title>
        <style>
          body{font:15px -apple-system,"Segoe UI",sans-serif;display:flex;align-items:center;justify-content:center;
               height:100vh;margin:0;background:#f5f5f7;color:#1d1d1f}
          .card{background:#fff;padding:28px 36px;border-radius:14px;box-shadow:0 10px 30px rgba(0,0,0,.12);text-align:center}
          .spin{width:26px;height:26px;border:3px solid #d0d0d5;border-top-color:#0a84ff;border-radius:50%;
                animation:s 0.8s linear infinite;margin:0 auto 14px}
          @keyframes s{to{transform:rotate(360deg)}}
        </style></head>
        <body><div class="card"><div class="spin"></div>{{SENDING}}</div>
        <form id="f" method="POST" enctype="multipart/form-data" action="{{URL}}">
          <input type="file" name="encoded_image" id="i" style="display:none">
        </form>
        <script>
          const b64 = "{{B64}}";
          const bin = atob(b64), arr = new Uint8Array(bin.length);
          for (let k = 0; k < bin.length; k++) arr[k] = bin.charCodeAt(k);
          const file = new File([arr], "{{NAME}}", { type: "{{MIME}}" });
          const dt = new DataTransfer();
          dt.items.add(file);
          document.getElementById("i").files = dt.files;
          document.getElementById("f").submit();
        </script></body></html>
        """;
}
