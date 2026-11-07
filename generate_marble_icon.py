import math
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageOps
import os

def create_marble_texture(width, height, seed=42):
    rng = np.random.default_rng(seed)
    
    # Generate multi-scale Perlin-like / value noise grid
    def make_noise(w, h, scale):
        gw, gh = int(w / scale) + 3, int(h / scale) + 3
        grid = rng.standard_normal((gh, gw)).astype(np.float32)
        # Bilinear interpolation
        y = np.linspace(0, gh - 1, h, endpoint=False)
        x = np.linspace(0, gw - 1, w, endpoint=False)
        xi = x.astype(int)
        yi = y.astype(int)
        xf = x - xi
        yf = y - yi
        
        top_left = grid[yi[:, None], xi[None, :]]
        top_right = grid[yi[:, None], xi[None, :] + 1]
        bot_left = grid[yi[:, None] + 1, xi[None, :]]
        bot_right = grid[yi[:, None] + 1, xi[None, :] + 1]
        
        top = top_left * (1 - xf) + top_right * xf
        bot = bot_left * (1 - xf) + bot_right * xf
        return top * (1 - yf[:, None]) + bot * yf[:, None]

    # Combine turbulence octaves
    turb = np.zeros((height, width), dtype=np.float32)
    scales = [160, 80, 40, 20, 10, 5]
    weights = [1.0, 0.5, 0.25, 0.125, 0.062, 0.031]
    
    for s, w in zip(scales, weights):
        n = make_noise(width, height, s)
        turb += np.abs(n) * w

    # Diagonal coordinate for natural marble geological shear
    y_idx, x_idx = np.mgrid[0:height, 0:width]
    # Shear angle ~ 35 degrees
    u = (x_idx * 0.75 + y_idx * 0.55) / 38.0
    
    # Marble vein equation: sin(u + turbulence * amp)
    marble_pattern = np.sin(u + turb * 4.2)
    marble_pattern = np.clip((marble_pattern + 1.0) * 0.5, 0.0, 1.0)
    
    # Secondary fine vein network
    u2 = (x_idx * -0.5 + y_idx * 0.85) / 22.0
    turb2 = make_noise(width, height, 18) * 0.5 + make_noise(width, height, 8) * 0.25
    vein2 = np.abs(np.sin(u2 + turb2 * 3.5))
    vein2 = np.power(vein2, 3.2) # Sharp fine cracks

    # Color mapping:
    # 0 = Dark charcoal/slate vein (#262D38)
    # 0.35 = Soft smoke gray (#6E7B8E)
    # 0.65 = Pale mineral white (#E8ECF2)
    # 1.0 = Pure Carrara white (#FCFDFE)
    
    # Base luminance curve: high white plateau with sharp vein valleys
    base_luma = 0.82 + 0.16 * (marble_pattern ** 0.6)
    
    # Darken for veins
    vein_dip = np.exp(-((marble_pattern - 0.22) ** 2) / 0.035) * 0.42
    vein_dip += (1.0 - vein2) * 0.28 * (1.0 - marble_pattern * 0.5)
    
    luma = np.clip(base_luma - vein_dip, 0.18, 0.99)
    
    # Fine quartz grain
    grain = (rng.standard_normal((height, width)) * 0.015).astype(np.float32)
    luma = np.clip(luma + grain, 0.0, 1.0)
    
    # Map to RGB
    # Warm / cool subtle tones in Carrara marble
    r = luma * 255.0
    g = (luma * 0.985 + 0.01) * 255.0
    b = (luma * 0.97 + 0.02) * 255.0
    
    # Tint veins slightly cooler charcoal blue-gray
    vein_mask = np.clip(vein_dip * 2.0, 0.0, 1.0)
    r = r * (1.0 - vein_mask * 0.12)
    g = g * (1.0 - vein_mask * 0.08)
    b = b * (1.0 - vein_mask * 0.02) # slightly more blue in the veins
    
    rgb = np.stack([r, g, b], axis=-1).astype(np.uint8)
    return Image.fromarray(rgb, mode='RGB')

def build_icon(size=512):
    # 1. Canvas: Deep obsidian black with subtle vignette and sleek squircle
    canvas = Image.new("RGBA", (size, size), (5, 7, 11, 255))
    draw = ImageDraw.Draw(canvas)
    
    # Background radial glow / subtle slate ambient
    bg_arr = np.zeros((size, size, 4), dtype=np.float32)
    cy, cx = np.mgrid[0:size, 0:size]
    dist = np.sqrt((cx - size/2)**2 + (cy - size/2)**2) / (size * 0.707)
    # Slate glow at center (#0F1724), pitch black at corners (#020306)
    bg_lum = np.clip(1.0 - dist * 0.9, 0.0, 1.0)
    bg_arr[..., 0] = 3.0 + bg_lum * 10.0
    bg_arr[..., 1] = 5.0 + bg_lum * 14.0
    bg_arr[..., 2] = 8.0 + bg_lum * 22.0
    bg_arr[..., 3] = 255.0
    bg_img = Image.fromarray(bg_arr.astype(np.uint8), mode='RGBA')
    canvas = Image.alpha_composite(canvas, bg_img)
    draw = ImageDraw.Draw(canvas)
    
    # Rounded squircle boundary border
    margin = int(size * 0.035)
    radius = int(size * 0.22)
    border_rect = [margin, margin, size - margin, size - margin]
    
    # Subtle dark plate inside
    draw.rounded_rectangle(border_rect, radius=radius, fill=(8, 11, 18, 255), outline=(32, 42, 60, 255), width=2)
    
    # Inner rim highlight
    inner_margin = margin + 2
    draw.rounded_rectangle([inner_margin, inner_margin, size - inner_margin, size - inner_margin],
                           radius=radius - 2, fill=None, outline=(18, 24, 36, 160), width=1)

    # 2. Render letter 'M' mask
    # Try Georgia Bold first for classical Roman sculpted marble feel
    font_path = "C:/Windows/Fonts/georgiab.ttf"
    if not os.path.exists(font_path):
        font_path = "C:/Windows/Fonts/timesbd.ttf"
    if not os.path.exists(font_path):
        font_path = "C:/Windows/Fonts/segoeuib.ttf"
        
    font_size = int(size * 0.68)
    font = ImageFont.truetype(font_path, font_size)
    
    # Measurement for exact centering
    mask_img = Image.new("L", (size, size), 0)
    mask_draw = ImageDraw.Draw(mask_img)
    
    bbox = font.getbbox("M")
    bw = bbox[2] - bbox[0]
    bh = bbox[3] - bbox[1]
    
    # Center position
    ox = (size - bw) // 2 - bbox[0]
    oy = (size - bh) // 2 - bbox[1] - int(size * 0.015)
    
    mask_draw.text((ox, oy), "M", fill=255, font=font)
    
    # 3. Create 3D Bevel & Lighting
    # Distance transforms & blurred normal map for chiseled marble bevel
    mask_np = np.array(mask_img).astype(np.float32) / 255.0
    
    # Edge gradient using Gaussian blur difference (unsharp / height map)
    h_broad = np.array(mask_img.filter(ImageFilter.GaussianBlur(radius=8))).astype(np.float32) / 255.0
    h_sharp = np.array(mask_img.filter(ImageFilter.GaussianBlur(radius=2))).astype(np.float32) / 255.0
    height_map = mask_np * 0.45 + h_sharp * 0.35 + h_broad * 0.20
    
    # Compute Sobel / central differences for surface normals
    # Light from top-left: azimuth = 315 deg, elevation = 50 deg
    lx = -0.55
    ly = -0.65
    lz = 0.52
    length = math.sqrt(lx*lx + ly*ly + lz*lz)
    lx /= length; ly /= length; lz /= length
    
    gy, gx = np.gradient(height_map * 22.0)
    # Surface normal: (-gx, -gy, 1.0) normalized
    nx = -gx
    ny = -gy
    nz = np.ones_like(gx)
    n_len = np.sqrt(nx*nx + ny*ny + nz*nz)
    nx /= n_len; ny /= n_len; nz /= n_len
    
    # Diffuse dot product
    dot_diffuse = nx * lx + ny * ly + nz * lz
    diffuse = np.clip(dot_diffuse * 0.45 + 0.65, 0.45, 1.25)
    
    # Specular highlight (polished marble reflection)
    # Blinn-Phong half vector
    # View vector is (0, 0, 1)
    hx = lx; hy = ly; hz = lz + 1.0
    hlen = math.sqrt(hx*hx + hy*hy + hz*hz)
    hx /= hlen; hy /= hlen; hz /= hlen
    dot_spec = np.clip(nx * hx + ny * hy + nz * hz, 0.0, 1.0)
    specular = np.power(dot_spec, 24.0) * 0.55
    
    # 4. Generate marble texture and apply lighting & mask
    marble = create_marble_texture(size, size, seed=108)
    marble_np = np.array(marble).astype(np.float32)
    
    # Lit marble: multiply by diffuse and add specular
    lit_marble = marble_np * diffuse[..., None] + (specular[..., None] * 255.0)
    lit_marble = np.clip(lit_marble, 0.0, 255.0).astype(np.uint8)
    
    marble_lit_img = Image.fromarray(lit_marble, mode='RGB')
    
    # Convert mask to RGBA
    letter_rgba = marble_lit_img.convert("RGBA")
    letter_rgba.putalpha(mask_img)
    
    # 5. Drop Shadows (casting onto black pedestal)
    # Deep soft shadow
    shadow_soft = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    s_draw = ImageDraw.Draw(shadow_soft)
    s_draw.text((ox + 4, oy + 12), "M", fill=(0, 0, 0, 180), font=font)
    shadow_soft = shadow_soft.filter(ImageFilter.GaussianBlur(radius=14))
    
    # Contact shadow (close & dark)
    shadow_contact = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sc_draw = ImageDraw.Draw(shadow_contact)
    sc_draw.text((ox + 2, oy + 5), "M", fill=(0, 0, 0, 220), font=font)
    shadow_contact = shadow_contact.filter(ImageFilter.GaussianBlur(radius=4))
    
    # Composite: Background -> Soft Shadow -> Contact Shadow -> Marble Letter
    canvas = Image.alpha_composite(canvas, shadow_soft)
    canvas = Image.alpha_composite(canvas, shadow_contact)
    canvas = Image.alpha_composite(canvas, letter_rgba)
    
    # Chiseled rim highlight: subtle white glow on top-left edge
    highlight_img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    h_draw = ImageDraw.Draw(highlight_img)
    h_draw.text((ox - 1, oy - 1), "M", fill=(255, 255, 255, 60), font=font)
    highlight_img = highlight_img.filter(ImageFilter.GaussianBlur(radius=1.5))
    canvas = Image.alpha_composite(canvas, highlight_img)

    return canvas

def main():
    out_dir = r"X:\MyCompiler\Assets"
    os.makedirs(out_dir, exist_ok=True)
    
    print("Generating White Marble 'M' icon...")
    img512 = build_icon(512)
    
    png_path = os.path.join(out_dir, "mycompiler.png")
    img512.save(png_path, "PNG")
    print(f"Saved high-res PNG to: {png_path}")
    
    ico_path = os.path.join(out_dir, "mycompiler.ico")
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    img512.save(ico_path, format="ICO", sizes=sizes)
    print(f"Saved multi-res ICO to: {ico_path}")
    
    # Also save to src/Assets if exists
    src_assets = r"X:\MyCompiler\src\Assets"
    if os.path.exists(src_assets):
        img512.save(os.path.join(src_assets, "mycompiler.png"), "PNG")
        img512.save(os.path.join(src_assets, "mycompiler.ico"), format="ICO", sizes=sizes)
        print("Mirrored icon to src/Assets")

if __name__ == "__main__":
    main()
