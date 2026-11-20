cbuffer Camera : register(b0)
{
    row_major float4x4 ViewProjection;
    float4 Eye;
    float4 Frame; // seconds, inverse width, inverse height, light preset
    float4 LightPosition[3]; // position and radius
    float4 LightColour[3];
    float4 DepthInfo; // near, far, samples
    float4 PostProcess; // reference exposure (zero selects the studio pipeline)
};

cbuffer Surface : register(b1)
{
    float4 Maps; // normal packing, reflectance packing, roughness override, opacity mode
    float4 Tint; // linear emission tint, material opacity
    float4 Motion; // UV scroll, emission strength, lighting mix
    float4 Edge; // RGB and Fresnel falloff
    float4 Modulation; // brightness, particle vertex colour enabled, UV transform enabled, soft depth distance
    float4 UvTransform; // tile XY and movement XY
};

Texture2D Base : register(t0);
Texture2D Glow : register(t1);
Texture2D NormalMap : register(t2);
Texture2D Reflectance : register(t3);
Texture2D EffectMask : register(t4);
Texture2D<float> SceneDepth : register(t16);
Texture2DMS<float> SceneDepthMS : register(t17);
SamplerState Wrap : register(s0);
SamplerState ClampUV : register(s1);

struct Vertex { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 colour : COLOR0; };
// color matches PmcPixelIn of the composed game material shaders (vertex colour; the preview meshes have none).
struct Fragment { float4 clip : SV_Position; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float2 uv : TEXCOORD2; float4 color : COLOR0; };

// x: offset along the normal (inflated outline shells of game materials).
cbuffer VertexEffects : register(b1) { float4 VertexOffset; };

Fragment MeshVertex(Vertex input)
{
    Fragment output;
    float3 world = input.position;
    if (VertexOffset.x != 0 && dot(input.normal, input.normal) > 0) world += normalize(input.normal) * VertexOffset.x;
    output.world = world;
    output.clip = mul(float4(world, 1), ViewProjection);
    output.normal = input.normal;
    output.uv = input.uv;
    output.color = input.colour;
    return output;
}

float3 Directional(float3 n, float3 v, float3 l, float3 colour, float3 diffuse, float3 f0, float rough)
{
    float nv = max(.001, dot(n, v));
    float nl = max(0, dot(n, l));
    float3 h = normalize(l + v + .0001);
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));
    float a = rough * rough;
    float a2 = a * a;
    float denominator = nh * nh * (a2 - 1) + 1;
    float d = a2 / max(.00001, 3.14159 * denominator * denominator);
    float vis = 0.5 / max(.001, nl * sqrt(nv * nv * (1 - a2) + a2) + nv * sqrt(nl * nl * (1 - a2) + a2));
    float3 f = f0 + (1 - f0) * pow(1 - vh, 5);
    return colour * nl * ((1 - f) * diffuse * .31831 + min(d * vis, 8) * f);
}

float DistanceAtDepth(float depth)
{
    return DepthInfo.x * DepthInfo.y / max(.00001, DepthInfo.y - depth * (DepthInfo.y - DepthInfo.x));
}

float DepthFade(float4 position)
{
    if (Modulation.w <= 0) return 1;
    int2 pixel = int2(position.xy);
    float behind;
    if (DepthInfo.z > 1)
    {
        behind = 1;
        [unroll] for (int i = 0; i < 4; ++i) behind = min(behind, SceneDepthMS.Load(pixel, i));
    }
    else behind = SceneDepth.Load(int3(pixel, 0));
    if (behind >= .99999) return 1;
    return saturate((DistanceAtDepth(behind) - DistanceAtDepth(position.z)) / Modulation.w);
}

float4 MeshPixel(Fragment input) : SV_Target
{
    float2 uv = input.uv;
    if (Modulation.z > 0) uv = uv * UvTransform.xy + UvTransform.zw * Frame.x;
    if (Maps.w >= 3 && Motion.w != 0)
    {
        float s, c;
        sincos(Frame.x * Motion.w, s, c);
        uv = mul(uv - .5, float2x2(c, -s, s, c)) + .5;
    }
    float4 albedo = Base.Sample(Wrap, uv);
    float4 glow = Glow.Sample(Wrap, uv + Motion.xy * Frame.x);
    float3 emission = glow.rgb * Tint.rgb * Motion.z;
    float4 particleColour = Modulation.y > 0 ? input.color : float4(1, 1, 1, 1);
    if (Maps.w == 3)
        return float4(emission * particleColour.rgb, glow.a * Tint.a * particleColour.a * DepthFade(input.clip));
    if (Maps.w == 4)
        return float4(lerp(1, albedo.rgb, albedo.a * Tint.a * particleColour.a * DepthFade(input.clip)), 0);
    if (Maps.w == 5)
    {
        float shadow = 1 - saturate(length((uv - .5) * 2));
        return float4(0, 0, 0, shadow * shadow * Tint.a);
    }
    if (Maps.w == 6)
    {
        float2 tile = input.world.xz / 46;
        float2 seam = abs(frac(tile) - .5);
        float mortar = smoothstep(.473, .495, max(seam.x, seam.y));
        float stone = frac(sin(dot(floor(tile),float2(37.3,57.1)))*43758.5453);
        float radial = 1 - saturate(length((uv - .5) * 2));
        float3 floorColour = float3(.038,.043,.043) * (.82 + .18*stone - .24*mortar);
        return float4(floorColour, radial * radial * .62);
    }
    if (Maps.w == 1) clip(albedo.a - .5);
    albedo.rgb *= Modulation.x;

    float3 view = normalize(Eye.xyz - input.world);
    float3 n = normalize(input.normal + float3(0, .00001, 0));
    if (dot(n, view) < 0) n = -n;
    float4 data = NormalMap.Sample(Wrap, uv);
    if (Maps.x > 0)
    {
        float3 dx = ddx(input.world), dy = ddy(input.world);
        float2 du = ddx(uv), dv = ddy(uv);
        float det = du.x * dv.y - du.y * dv.x;
        if (abs(det) > .00000001)
        {
            float3 tangent = (dx * dv.y - dy * du.y) * sign(det);
            tangent -= n * dot(tangent, n);
            tangent *= rsqrt(max(.000001, dot(tangent, tangent)));
            float3 b = cross(n, tangent);
            b *= sign(dot(b, (dy * du.x - dx * dv.x) * sign(det)));
            float2 xy = data.rg * 2 - 1;
            n = normalize(tangent * xy.x + b * xy.y + n * sqrt(max(.01, 1 - dot(xy, xy))));
        }
    }

    float4 reflectance = Reflectance.Sample(Wrap, uv);
    float metal = Maps.y == 1 ? reflectance.r : Maps.y == 4 ? 1 : 0;
    float3 f0 = lerp(float3(.04, .04, .04), albedo.rgb, metal);
    if (Maps.y == 2) f0 = .02 + reflectance.rgb * .22;
    if (Maps.y == 3) f0 = .018 + reflectance.aaa * .20;
    if (Maps.y == 4) f0 = .025 + reflectance.rgb * .95;
    float rough = Maps.z >= 0 ? Maps.z : .62;
    float ao = 1;
    if (Maps.x == 2)
    {
        ao = lerp(.55, 1, data.b);
        rough = Maps.z == -2 ? data.a : 1 - data.a;
    }
    rough = clamp(rough, .16, .96);

    float3 diffuse = albedo.rgb * (1 - metal);
    float facing = saturate(dot(n, view));
    float3 reflected = reflect(-view, n);
    float specularRoom = (.18 + .30 * smoothstep(-.3, .8, reflected.y)) * (1 - .55 * rough);
    float3 room = lerp(float3(.16, .20, .27), float3(.32, .34, .40), saturate(.5 + .5 * n.y));
    float3 lit = (diffuse * room + f0 * specularRoom * (1.0 + 1.35 * pow(1 - facing, 4))) * ao;
    bool warm = Frame.w == 1;
    lit += Directional(n, view, normalize(float3(-.63, .76, -.53)), warm ? float3(4.1, 2.65, 1.4) : float3(2.9, 2.82, 2.65), diffuse, f0, rough);
    lit += Directional(n, view, normalize(float3(.77, .45, .58)), warm ? float3(.83, 1.31, 2.6) : float3(1.25, 1.65, 2.65), diffuse, f0, rough);
    lit += Directional(n, view, normalize(float3(.17, .32, -.94)), float3(.65, .68, .78), diffuse, f0, rough);
    if (Frame.w == 2) lit *= .30;
    [unroll] for (int i = 0; i < 3; i++)
    {
        float3 towardLight = LightPosition[i].xyz - input.world;
        float distance = length(towardLight);
        float falloff = 1 - saturate(distance / max(1, LightPosition[i].w));
        lit += Directional(n, view, towardLight / max(1, distance), LightColour[i].rgb * (falloff * falloff), diffuse, f0, rough);
    }
    lit = lerp(albedo.rgb, lit, Motion.w);
    float fresnel = pow(1 - facing, max(.15, Edge.a));
    return float4(lit + emission * glow.a + Edge.rgb * fresnel * EffectMask.Sample(Wrap, uv).r,
        Maps.w == 2 ? albedo.a * Tint.a * DepthFade(input.clip) : 1);
}

struct Screen { float4 clip : SV_Position; float2 uv : TEXCOORD0; };
Screen ScreenVertex(uint id : SV_VertexID)
{
    Screen output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.clip = float4(output.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

float3 Studio(float2 uv)
{
    float radial = saturate(1 - length((uv - float2(.50, .48)) * float2(1.45, 1.25)));
    return lerp(float3(.004, .006, .008), float3(.039, .047, .046), radial * radial);
}

float4 BackgroundPixel(Screen input) : SV_Target { return float4(Studio(input.uv) * (Frame.w == 2 ? .62 : 1), 1); }

float4 RefractPixel(Screen input) : SV_Target
{
    float2 field = Glow.SampleLevel(ClampUV, input.uv, 0).xy;
    float2 offset = clamp(field * 8.0, -12.0, 12.0) * Frame.yz;
    return Base.SampleLevel(ClampUV, input.uv + offset, 0);
}

static const float BloomCutoff = 0.95;
static const float BloomKnee = 0.35;
static const float BloomIntensity = 0.65;
static const float Exposure = 1.6;

float3 BrightPass(float3 colour)
{
    colour = max(0, colour);
    float peak = max(colour.r, max(colour.g, colour.b));
    float soft = clamp(peak - BloomCutoff + BloomKnee, 0, 2 * BloomKnee);
    float amount = max(peak - BloomCutoff, soft * soft / (4 * BloomKnee));
    return colour * (min(amount, 32.0) / max(peak, 1e-4));
}

float3 BloomSeed(float3 colour)
{
    if (PostProcess.x <= 0) return BrightPass(colour);

    // The reference path extracts highlights in the exposed scene using mean RGB.
    float3 exposed = max(0, colour) * PostProcess.x;
    float brightness = dot(exposed, float3(1.0 / 3, 1.0 / 3, 1.0 / 3));
    float contribution = clamp(brightness - 1, 0, 16);
    return exposed * (contribution / max(brightness, 1e-4));
}

float4 BloomCutoffPixel(Screen input) : SV_Target
{
    float2 uv = input.uv, d = Frame.yz * .5;
    // Extract highlights before averaging so thin, bright strands still contribute.
    float3 bright = BloomSeed(Base.SampleLevel(ClampUV, uv + float2(-d.x, -d.y), 0).rgb)
                  + BloomSeed(Base.SampleLevel(ClampUV, uv + float2( d.x, -d.y), 0).rgb)
                  + BloomSeed(Base.SampleLevel(ClampUV, uv + float2(-d.x,  d.y), 0).rgb)
                  + BloomSeed(Base.SampleLevel(ClampUV, uv + float2( d.x,  d.y), 0).rgb);
    return float4(bright * .25, 1);
}

float4 BlurBloom(float2 uv, float2 axis)
{
    uint w, h;
    Base.GetDimensions(w, h);
    float2 stepUV = axis / float2(w, h);
    // Normalized Gaussian (sigma 8 at half resolution). Each linear tap combines two texels.
    static const float2 taps[11] = {
        float2(1.4941, .098294), float2(3.4863, .090935), float2(5.4785, .079049),
        float2(7.4707, .064569), float2(9.4630, .049558), float2(11.4552, .035741),
        float2(13.4475, .024220), float2(15.4397, .015423), float2(17.4321, .009228),
        float2(19.4244, .005188), float2(21.4168, .002741)
    };
    float3 sum = Base.SampleLevel(ClampUV, uv, 0).rgb * .050113;
    [unroll] for (int i = 0; i < 11; i++)
    {
        float2 offset = stepUV * taps[i].x;
        sum += (Base.SampleLevel(ClampUV, uv - offset, 0).rgb
              + Base.SampleLevel(ClampUV, uv + offset, 0).rgb) * taps[i].y;
    }
    return float4(sum, 1);
}

float4 BloomHorizontalPixel(Screen input) : SV_Target { return BlurBloom(input.uv, float2(1, 0)); }
float4 BloomVerticalPixel(Screen input) : SV_Target { return BlurBloom(input.uv, float2(0, 1)); }

float3 ReferencePostProcess(float3 rgb, float2 uv)
{
    float3 exposed = max(0, rgb) * PostProcess.x;
    float3 mapped = PostProcess.x > 1 ? 1 - exp(-exposed) : exposed;
    // Wider mips contribute progressively less to keep the glow near the source.
    static const float weights[5] = {.32, .26, .20, .14, .08};
    float3 bloom = 0;
    [unroll] for (int i = 0; i < 5; i++)
        bloom += Glow.SampleLevel(ClampUV, uv, i + 1).rgb * weights[i];
    return saturate(mapped + bloom * BloomIntensity);
}

float4 FinishPixel(Screen input) : SV_Target
{
    float2 uv = input.uv, delta = Frame.yz;
    float3 centre = Base.Sample(ClampUV, uv).rgb;
    float3 top = Base.Sample(ClampUV, uv - float2(0, delta.y)).rgb;
    float3 bottom = Base.Sample(ClampUV, uv + float2(0, delta.y)).rgb;
    float3 left = Base.Sample(ClampUV, uv - float2(delta.x, 0)).rgb;
    float3 right = Base.Sample(ClampUV, uv + float2(delta.x, 0)).rgb;
    float edge = length(top - bottom) + length(left - right);
    float3 rgb = lerp(centre, (top + bottom + left + right) * .25, .27 * smoothstep(.10, .52, edge));
    if (PostProcess.x > 0)
        rgb = ReferencePostProcess(rgb, uv);
    else
    {
        bool muted = Frame.w == 2;
        rgb += Glow.SampleLevel(ClampUV, uv, 0).rgb * (muted ? .38 : BloomIntensity);
        rgb = max(0, rgb);
        float peak = max(rgb.r, max(rgb.g, rgb.b));
        float exposure = muted ? 1.30 : Exposure;
        float3 perChannel = 1 - exp(-rgb * exposure);
        // Keep the colour of bright emission as it rolls off, with a soft transition from midtones.
        float3 huePreserved = rgb * ((1 - exp(-peak * exposure)) / max(peak, 1e-4));
        rgb = lerp(perChannel, huePreserved, (muted ? .88 : .65) * smoothstep(.5, 2.0, peak));
    }
    float3 display = lerp(1.055 * pow(rgb, 1.0 / 2.4) - .055, 12.92 * rgb, step(rgb, .0031308));
    return float4(display, 1);
}
