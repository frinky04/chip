namespace Chip.Editor.Rendering;

static class Shaders
{
    public const string MeshVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNrm;
        layout(location=2) in vec2 aUv;
        uniform mat4 uViewProj;
        out vec3 vNrm;
        out vec2 vUv;
        out vec3 vWorld;
        void main() { vNrm = aNrm; vUv = aUv; vWorld = aPos; gl_Position = uViewProj * vec4(aPos, 1.0); }
        """;

    // uMode: 0 lit (per-direction shading keeps flat blockout surfaces readable without real lights),
    // 1 unlit (texture only), 3 UV checker. The checker has 64-texel cells (12.5 cm at the default 512 px/m), lines on
    // texture tile borders, and a tint for texel density off target: red = denser than uDensity, blue = stretched.
    // uSurface 1: a surface's look instead of a texture, in world space on the face's dominant plane, so it needs no UVs
    // and stays crisp at any size (uPattern 0 solid, 1 stripes, 2 checker). uAlpha < 1: drawn see-through.
    public const string MeshFs = """
        #version 330 core
        in vec3 vNrm;
        in vec2 vUv;
        in vec3 vWorld;
        uniform sampler2D uTex;
        uniform int uMode;
        uniform vec2 uTexSize;
        uniform float uDensity;
        uniform int uSurface;
        uniform int uPattern;
        uniform vec3 uSurfColor;
        uniform float uAlpha;
        out vec4 frag;
        vec3 surface(vec3 n) {
            vec3 a = abs(n);
            vec2 p = a.x >= a.y && a.x >= a.z ? vWorld.zy : a.y >= a.z ? vWorld.xz : vWorld.xy;
            float k = 0.0;
            if (uPattern == 1) {
                float s = (p.x + p.y) * 2.0; // 25 cm stripes across the diagonal
                float w = fwidth(s);
                float f = abs(fract(s) - 0.5);
                k = smoothstep(0.25 - w, 0.25 + w, f);
                k = mix(k, 0.5, smoothstep(0.3, 0.6, w)); // fade to the average where stripes get thinner than a pixel
            } else if (uPattern == 2) {
                vec2 c = floor(p * 2.0);
                k = mod(c.x + c.y, 2.0);
                k = mix(k, 0.5, smoothstep(0.3, 0.6, max(fwidth(p.x), fwidth(p.y)) * 2.0));
            }
            return mix(uSurfColor, uSurfColor * 0.55, k);
        }
        vec3 checker() {
            vec2 t = vUv * uTexSize / 64.0;
            float c = mod(floor(t.x) + floor(t.y), 2.0);
            vec2 ft = fwidth(t);
            c = mix(c, 0.5, smoothstep(0.25, 0.75, max(ft.x, ft.y))); // fade to grey where cells get smaller than a pixel or two
            vec3 col = mix(vec3(0.34), vec3(0.72), c);
            vec2 fu = max(fwidth(vUv), vec2(1e-6));
            vec2 tile = abs(fract(vUv + 0.5) - 0.5) / fu;
            float lineA = (1.0 - min(min(tile.x, tile.y), 1.0)) * (1.0 - smoothstep(0.05, 0.25, max(fu.x, fu.y)));
            col = mix(col, vec3(0.12, 0.62, 0.70), lineA);
            vec2 tx = vUv * uTexSize;
            float wx = length(dFdx(vWorld)), wy = length(dFdy(vWorld));
            float dx = wx > 1e-6 ? length(dFdx(tx)) / wx : uDensity;
            float dy = wy > 1e-6 ? length(dFdy(tx)) / wy : uDensity;
            float r = log2(max(0.5 * (dx + dy), 1e-3) / uDensity);
            vec3 tint = r > 0.0 ? vec3(0.95, 0.30, 0.25) : vec3(0.25, 0.45, 0.95);
            return mix(col, col * tint * 1.6, clamp(abs(r) * 0.45 - 0.08, 0.0, 0.55));
        }
        void main() {
            vec3 n = normalize(vNrm);
            float d = dot(n, normalize(vec3(0.35, 1.0, 0.6))) * 0.5 + 0.5;
            vec3 c;
            if (uSurface == 1) c = uMode == 1 ? surface(n) : surface(n) * (0.48 + 0.52 * d);
            else if (uMode == 3) c = checker() * (0.62 + 0.38 * d);
            else if (uMode == 1) c = texture(uTex, vUv).rgb;
            else c = texture(uTex, vUv).rgb * (0.48 + 0.52 * d);
            frag = vec4(c, uAlpha);
        }
        """;

    // Overlay geometry is pulled slightly toward the eye so it wins depth ties with the surface it sits on.
    public const string ColorVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec4 aCol;
        uniform mat4 uViewProj;
        uniform vec3 uEye;
        uniform float uPull;
        uniform float uPointSize;
        out vec4 vCol;
        void main() {
            vec3 p = aPos + (uEye - aPos) * uPull;
            vCol = aCol;
            gl_PointSize = uPointSize;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    // Expands each line into a screen-space quad of uWidth pixels (core profile has no wide lines), and passes the
    // distance along the line in pixels so the fragment shader can dash it.
    public const string LineGs = """
        #version 330 core
        layout(lines) in;
        layout(triangle_strip, max_vertices = 4) out;
        in vec4 vCol[];
        out vec4 gCol;
        noperspective out float gDist;
        uniform vec2 uViewport;
        uniform float uWidth;
        void main() {
            vec4 a = gl_in[0].gl_Position, b = gl_in[1].gl_Position;
            const float e = 0.01;
            if (a.w < e && b.w < e) return;
            if (a.w < e) a = mix(a, b, (e - a.w) / (b.w - a.w));
            else if (b.w < e) b = mix(b, a, (e - b.w) / (a.w - b.w));
            vec2 hv = uViewport * 0.5;
            vec2 d = b.xy / b.w * hv - a.xy / a.w * hv;
            float len = length(d);
            if (dot(d, d) < 1e-8) d = vec2(1.0, 0.0);
            vec2 n = normalize(vec2(-d.y, d.x)) * uWidth * 0.5 / hv;
            gCol = vCol[0]; gDist = 0.0; gl_Position = a + vec4(n * a.w, 0, 0); EmitVertex();
            gCol = vCol[0]; gDist = 0.0; gl_Position = a - vec4(n * a.w, 0, 0); EmitVertex();
            gCol = vCol[1]; gDist = len; gl_Position = b + vec4(n * b.w, 0, 0); EmitVertex();
            gCol = vCol[1]; gDist = len; gl_Position = b - vec4(n * b.w, 0, 0); EmitVertex();
            EndPrimitive();
        }
        """;

    public const string LineFs = """
        #version 330 core
        in vec4 gCol;
        noperspective in float gDist;
        uniform float uAlpha;
        uniform vec4 uTint;
        uniform float uDash;
        out vec4 frag;
        void main() {
            if (uDash > 0.0 && mod(gDist, uDash * 2.0) > uDash) discard;
            vec4 c = gCol * uTint;
            frag = vec4(c.rgb, c.a * uAlpha);
        }
        """;

    // A negative alpha asks for diagonal screen-space stripes (dressing proxies in the blockout view).
    public const string ColorFs = """
        #version 330 core
        in vec4 vCol;
        uniform float uAlpha;
        out vec4 frag;
        void main() {
            float a = vCol.a;
            if (a < 0.0) {
                if (mod(gl_FragCoord.x + gl_FragCoord.y, 14.0) > 6.0) discard;
                a = -a;
            }
            frag = vec4(vCol.rgb, a * uAlpha);
        }
        """;

    // A quad in any plane (spanned by uU, uV around uCenter). Grid coordinates are measured from uOrigin along uU/uV.
    public const string GridVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        uniform mat4 uViewProj;
        uniform vec3 uCenter;
        uniform vec3 uU;
        uniform vec3 uV;
        uniform float uExtent;
        out vec3 vPos;
        void main() {
            vPos = uCenter + (uU * aPos.x + uV * aPos.y) * uExtent;
            gl_Position = uViewProj * vec4(vPos, 1.0);
        }
        """;

    // uFade: 0 = fade with distance from the eye (perspective ground), 1 = none (2D views), 2 = radial (construction grid).
    public const string GridFs = """
        #version 330 core
        in vec3 vPos;
        uniform float uGrid;
        uniform float uMajor;
        uniform float uMajor2;
        uniform vec3 uEye;
        uniform vec3 uOrigin;
        uniform vec3 uU;
        uniform vec3 uV;
        uniform vec4 uColU;
        uniform vec4 uColV;
        uniform int uFade;
        uniform vec3 uFadeCenter;
        uniform float uFadeRadius;
        uniform float uStrength;
        out vec4 frag;
        float lines(vec2 c, float size) {
            vec2 g = c / size;
            vec2 w = fwidth(g);
            vec2 l = abs(fract(g - 0.5) - 0.5) / w;
            float a = 1.0 - min(min(l.x, l.y), 1.0);
            return a * (1.0 - smoothstep(0.12, 0.4, max(w.x, w.y)));
        }
        void main() {
            vec3 d = vPos - uOrigin;
            vec2 c = vec2(dot(d, uU), dot(d, uV));
            // Minor lines, brighter majors, and the next tier up, which stays when the others thin out with distance.
            float a = max(lines(c, uGrid) * 0.10, max(lines(c, uMajor), lines(c, uMajor2)) * 0.26) * uStrength;
            vec3 col = vec3(0.62);
            vec2 w = fwidth(c) * 1.5;
            float au = (1.0 - min(abs(c.y) / w.y, 1.0)) * uColU.a; // the line along uU
            float av = (1.0 - min(abs(c.x) / w.x, 1.0)) * uColV.a; // the line along uV
            col = mix(col, uColU.rgb, au);
            col = mix(col, uColV.rgb, av);
            a = max(a, max(au, av) * 0.7);
            if (uFade == 0) a *= 1.0 - smoothstep(100.0, 400.0, length(vPos - uEye));
            else if (uFade == 2) a *= 1.0 - smoothstep(uFadeRadius * 0.35, uFadeRadius, length(vPos - uFadeCenter));
            if (a < 0.004) discard;
            frag = vec4(col, min(a, 1.0));
        }
        """;
}
