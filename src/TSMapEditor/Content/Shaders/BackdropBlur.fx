#pragma enable_d3d11_debug_symbols

#if OPENGL
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

// Shader for rendering window backgrounds (transparent glass effect).

float2 TexelSize;
float BlurRadius;
float2 BlurDirection;
float2 DownsampleStep;
float4 GlassTint;
float BackdropSaturation;
float NoiseStrength;
float4 ReflectionColor;
float ReflectionIntensity;
float2 ScreenUVOffset;
float2 ScreenUVScale;
float2 ScreenSize;

sampler2D SpriteTextureSampler : register(s0)
{
    Texture = (SpriteTexture); // this is set by SpriteBatch
    AddressU = clamp;
    AddressV = clamp;
    MipFilter = Linear;
    MinFilter = Linear;
    MagFilter = Linear;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float SoftBand(float value, float center, float halfWidth)
{
    return 1.0 - smoothstep(0.0, halfWidth, abs(value - center));
}

float InterleavedGradientNoise(float2 pixelPosition)
{
    return frac(52.9829189 * frac(dot(pixelPosition, float2(0.06711056, 0.00583715))));
}

float4 DownsamplePS(VertexShaderOutput input) : COLOR
{
    float2 uv = input.TextureCoordinates;
    float2 horizontalStep = float2(DownsampleStep.x, 0.0);
    float2 verticalStep = float2(0.0, DownsampleStep.y);

    // A separable 3x3 tent filter suppresses thin high-contrast details before
    // reducing resolution. In particular, text strokes cannot survive the
    // half- and quarter-scale stages as isolated block-shaped samples.
    float4 color = tex2D(SpriteTextureSampler, uv) * 4.0;
    color += tex2D(SpriteTextureSampler, uv + horizontalStep) * 2.0;
    color += tex2D(SpriteTextureSampler, uv - horizontalStep) * 2.0;
    color += tex2D(SpriteTextureSampler, uv + verticalStep) * 2.0;
    color += tex2D(SpriteTextureSampler, uv - verticalStep) * 2.0;
    color += tex2D(SpriteTextureSampler, uv + horizontalStep + verticalStep);
    color += tex2D(SpriteTextureSampler, uv + horizontalStep - verticalStep);
    color += tex2D(SpriteTextureSampler, uv - horizontalStep + verticalStep);
    color += tex2D(SpriteTextureSampler, uv - horizontalStep - verticalStep);
    color /= 16.0;
    color.a = 1.0;

    return color * input.Color;
}

float4 GaussianBlur(float2 uv)
{
    float blurScale = BlurRadius / 3.23076923;
    float2 sampleStep = TexelSize * BlurDirection * blurScale;

    // This five-fetch kernel uses bilinear filtering to approximate a smooth
    // nine-tap Gaussian in one dimension. Running it horizontally and then
    // vertically removes the discrete offset copies produced by the old
    // single-pass star-shaped kernel.
    float4 color = tex2D(SpriteTextureSampler, uv) * 0.22702703;
    color += tex2D(SpriteTextureSampler, uv + sampleStep * 1.38461538) * 0.31621622;
    color += tex2D(SpriteTextureSampler, uv - sampleStep * 1.38461538) * 0.31621622;
    color += tex2D(SpriteTextureSampler, uv + sampleStep * 3.23076923) * 0.07027027;
    color += tex2D(SpriteTextureSampler, uv - sampleStep * 3.23076923) * 0.07027027;

    return color;
}

float4 BlurOnlyPS(VertexShaderOutput input) : COLOR
{
    float4 color = GaussianBlur(input.TextureCoordinates);
    color.a = 1.0;
    return color * input.Color;
}

float4 GlassMaterialPS(VertexShaderOutput input) : COLOR
{
    float2 uv = input.TextureCoordinates;
    float4 color = GaussianBlur(uv);

    float luminance = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
    float3 materialColor = lerp(luminance.xxx, color.rgb, BackdropSaturation);

    float2 screenUV = ScreenUVOffset + uv * ScreenUVScale;

    // Screen-space bands make windows appear to move across a fixed reflection
    // when they are dragged, instead of carrying a painted-on highlight.
    float reflectionCoordinate = screenUV.x + screenUV.y * 0.55;
    float wideReflection = SoftBand(reflectionCoordinate, 0.58, 0.12) * 0.66;
    float narrowReflection = SoftBand(reflectionCoordinate, 0.79, 0.035) * 0.85;
    float narrowReflection2 = SoftBand(reflectionCoordinate, 1.1, 0.035) * 0.85;

    // A faint top-left bevel helps sell the thickness of the glass without
    // competing with the window's regular border.
    float topEdge = 1.0 - saturate(input.Position.y / 4.0);
    float leftEdge = 1.0 - saturate(input.Position.x / 4.0);
    float edgeReflection = max(topEdge, leftEdge) * 0.30;

    float reflection = wideReflection + narrowReflection + narrowReflection2 + edgeReflection;
    materialColor += ReflectionColor.rgb * reflection * ReflectionIntensity;

    // Match the previous alpha-blended tint while keeping the whole material
    // in the final Gaussian pass. Noise comes afterwards so it remains visible
    // through stronger glass tints.
    materialColor = lerp(materialColor, GlassTint.rgb, GlassTint.a);
    float noise = InterleavedGradientNoise(floor(screenUV * ScreenSize)) - 0.5;
    materialColor += noise * NoiseStrength;

    color.rgb = saturate(materialColor);
    color.a = 1.0;
    return color * input.Color;
}

technique Downsample
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL DownsamplePS();
    }
}

technique BlurOnly
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL BlurOnlyPS();
    }
}

technique GlassMaterial
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL GlassMaterialPS();
    }
};
