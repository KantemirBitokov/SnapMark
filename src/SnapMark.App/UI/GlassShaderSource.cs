namespace SnapMark.UI;

/// <summary>
/// Исходник шейдера Liquid Glass (SkSL). Отдельным файлом без зависимостей — его же компилирует
/// и проверяет тест tools/ShaderTest в GitHub Actions.
/// </summary>
public static class GlassShaderSource
{
    // Тело шейдера. SAMPLE(x) подставляется под синтаксис конкретной версии Skia.
    const string Body = @"
uniform shader backdrop;
uniform float2 size;
uniform float radius;
uniform float edge;
uniform float strength;
uniform float grain;
uniform float spec;

float sdf(float2 p) {
    float2 c = size * 0.5;
    float2 q = abs(p - c) - c + radius;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
}

float hash(float2 p) {
    return fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

half4 shade(float2 p) {
    float d = sdf(p);
    if (d > 0.5) { return half4(0.0); }
    float e = 0.75;
    float2 n = float2(sdf(p + float2(e, 0.0)) - sdf(p - float2(e, 0.0)),
                      sdf(p + float2(0.0, e)) - sdf(p - float2(0.0, e)));
    float nl = length(n);
    if (nl > 0.0001) { n = n / nl; } else { n = float2(0.0); }

    // 0 — глубоко внутри, 1 — у самой кромки.
    float t = clamp(1.0 + d / edge, 0.0, 1.0);
    float bend = t * t * strength;
    // Линзирование: у края видно то, что лежит ЗА границей, — фон изгибается вокруг стекла.
    float2 q = p + n * bend;
    half4 c = SAMPLE(q);
    half r = SAMPLE(q + n * bend * 0.14).r;
    half b = SAMPLE(q - n * bend * 0.14).b;
    c = half4(r, c.g, b, 1.0);

    // Блики от источника света сверху-слева (и слабый отражённый снизу-справа).
    float2 L = normalize(float2(-0.55, -0.85));
    // smoothstep вручную — в Skia 2.88 его нет.
    float rx = clamp(-d / (edge * 0.6), 0.0, 1.0);
    float rim = 1.0 - rx * rx * (3.0 - 2.0 * rx);
    float s = pow(max(dot(n, L), 0.0), 3.0) * rim * spec
            + pow(max(dot(n, -L), 0.0), 3.0) * rim * spec * 0.35;
    float g = (hash(floor(p)) - 0.5) * grain;
    c = half4(c.r + s + g, c.g + s + g, c.b + s + g, 1.0);

    // Мягкая кромка (сглаживание).
    float aa = clamp(0.5 - d, 0.0, 1.0);
    return half4(c.rgb * aa, aa);
}
";

    public static readonly string[] Variants =
    {
        Body.Replace("SAMPLE(", "backdrop.eval(") + "half4 main(float2 p) { return shade(p); }",
        Body.Replace("SAMPLE(", "sample(backdrop, ") + "half4 main(float2 p) { return shade(p); }",
        Body.Replace("SAMPLE(", "sample(backdrop, ") + "void main(float2 p, inout half4 color) { color = shade(p); }",
    };

}
