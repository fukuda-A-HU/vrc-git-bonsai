# Blender headless script: VRC Git Bonsai の土台モデル(台座・鉢・土・岩)を生成して FBX 出力する。
# 実行: blender --background --python make_bonsai_base.py
# 頂点カラーに簡易ライティング(ランバート+AO)を焼き込む。Unity 側は Unlit 頂点カラーシェーダーで表示する想定。
import bpy
import bmesh
import math
import os
from mathutils import Vector

OUT_DIR = os.path.join(os.getcwd(), "out")
os.makedirs(OUT_DIR, exist_ok=True)
FBX_PATH = os.path.join(OUT_DIR, "BonsaiBase.fbx")
BLEND_PATH = os.path.join(OUT_DIR, "BonsaiBase.blend")
RENDER_PATH = os.path.join(OUT_DIR, "BonsaiBase_preview.png")

SEG = 48          # 楕円リングの分割数
STAND_H = 0.20    # 台座の高さ(=鉢の底が乗る高さ)
POT_A = 0.30      # 鉢の長半径(X)
POT_B = 0.22      # 鉢の短半径(Y)
Z0 = STAND_H

LIGHT_DIR = Vector((0.35, -0.30, 0.89)).normalized()

# ---------------------------------------------------------------- utilities

def srgb(hexcode):
    """hex(sRGB) → シーンリニア。FLOAT カラー属性はリニア前提のため変換して格納する。"""
    def s2lin(c):
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r = int(hexcode[0:2], 16) / 255.0
    g = int(hexcode[2:4], 16) / 255.0
    b = int(hexcode[4:6], 16) / 255.0
    return (s2lin(r), s2lin(g), s2lin(b))


def _hash(ix, iy, iz):
    n = math.sin(ix * 12.9898 + iy * 78.233 + iz * 37.719) * 43758.5453
    return n - math.floor(n)


def value_noise(p, scale):
    """滑らかな値ノイズ(0..1)。苔のまだら模様用。"""
    x, y, z = p.x * scale, p.y * scale, p.z * scale
    ix, iy, iz = math.floor(x), math.floor(y), math.floor(z)
    fx, fy, fz = x - ix, y - iy, z - iz
    fx = fx * fx * (3 - 2 * fx)
    fy = fy * fy * (3 - 2 * fy)
    fz = fz * fz * (3 - 2 * fz)
    v = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = ((fx if dx else 1 - fx) * (fy if dy else 1 - fy) * (fz if dz else 1 - fz))
                v += w * _hash(ix + dx, iy + dy, iz + dz)
    return v


def shade(base, normal, ao=1.0):
    """ランバート+環境光を頂点カラーに焼き込む。"""
    lam = max(normal.dot(LIGHT_DIR), 0.0)
    k = (0.48 + 0.52 * lam) * ao
    return (base[0] * k, base[1] * k, base[2] * k, 1.0)


def oval_ring(a, b, f, z, seg=SEG):
    return [
        (a * f * math.cos(2 * math.pi * j / seg),
         b * f * math.sin(2 * math.pi * j / seg), z)
        for j in range(seg)
    ]


def loft(bm, rings, cap_start=False, cap_end=False):
    ring_verts = [[bm.verts.new(co) for co in ring] for ring in rings]
    for i in range(len(ring_verts) - 1):
        va, vb = ring_verts[i], ring_verts[i + 1]
        n = len(va)
        for j in range(n):
            bm.faces.new((va[j], va[(j + 1) % n], vb[(j + 1) % n], vb[j]))
    if cap_start:
        bm.faces.new(list(reversed(ring_verts[0])))
    if cap_end:
        bm.faces.new(ring_verts[-1])
    return ring_verts


def add_box(bm, center, size, taper=1.0):
    """center/size の直方体。taper<1 で底面を絞る(脚のテーパー用)。"""
    cx, cy, cz = center
    sx, sy, sz = size[0] / 2, size[1] / 2, size[2] / 2
    top = [(cx - sx, cy - sy, cz + sz), (cx + sx, cy - sy, cz + sz),
           (cx + sx, cy + sy, cz + sz), (cx - sx, cy + sy, cz + sz)]
    bot = [(cx - sx * taper, cy - sy * taper, cz - sz), (cx + sx * taper, cy - sy * taper, cz - sz),
           (cx + sx * taper, cy + sy * taper, cz - sz), (cx - sx * taper, cy + sy * taper, cz - sz)]
    vt = [bm.verts.new(co) for co in top]
    vb = [bm.verts.new(co) for co in bot]
    bm.faces.new(vt)
    bm.faces.new(list(reversed(vb)))
    for j in range(4):
        bm.faces.new((vb[j], vb[(j + 1) % 4], vt[(j + 1) % 4], vt[j]))


def finalize(name, bm, color_fn, smooth=False):
    """bmesh をメッシュ化し、頂点カラー(POINT ドメイン)を焼き込んでシーンに置く。"""
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if smooth:
        for f in bm.faces:
            f.smooth = True
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    attr = me.color_attributes.new(name="Col", type='FLOAT_COLOR', domain='POINT')
    for i, v in enumerate(me.vertices):
        attr.data[i].color = color_fn(v.co, v.normal)
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj

# ---------------------------------------------------------------- stand(台座)

WALNUT = srgb("4A3527")
WALNUT_TOP = srgb("5C4433")

def stand_color(co, normal):
    base = WALNUT_TOP if co.z > STAND_H - 0.055 else WALNUT
    ao = 0.75 if co.z < 0.13 else 1.0   # 天板下は影に沈める
    return shade(base, normal, ao)


def build_stand():
    bm = bmesh.new()
    # 天板(角をベベル)
    add_box(bm, (0, 0, STAND_H - 0.025), (0.92, 0.66, 0.05))
    bmesh.ops.bevel(bm, geom=list(bm.edges), offset=0.012, segments=2, profile=0.7, affect='EDGES')
    # 幕板(天板下の飾り板)
    add_box(bm, (0, 0, STAND_H - 0.075), (0.78, 0.52, 0.05))
    # 脚 4本(わずかにテーパー)
    for sx in (-1, 1):
        for sy in (-1, 1):
            add_box(bm, (sx * 0.375, sy * 0.245, 0.075), (0.055, 0.055, 0.15), taper=0.82)
    # 下段の棚板
    add_box(bm, (0, 0, 0.045), (0.70, 0.44, 0.022))
    return finalize("BonsaiStand", bm, stand_color)

# ---------------------------------------------------------------- pot(鉢)

POT_CLAY = srgb("7A4E3A")      # 焼締めの赤茶
POT_CLAY_DARK = srgb("55352A")

def pot_color(co, normal):
    t = max(0.0, min(1.0, (co.z - Z0) / 0.165))
    base = tuple(POT_CLAY_DARK[i] + (POT_CLAY[i] - POT_CLAY_DARK[i]) * t for i in range(3))
    e = (co.x / POT_A) ** 2 + (co.y / POT_B) ** 2
    ao = 0.68 if (e < 0.90 and co.z > Z0 + 0.05) else 1.0   # 内壁は影
    return shade(base, normal, ao)


def build_pot():
    bm = bmesh.new()
    profile = [
        (0.80, 0.020), (0.93, 0.075), (0.985, 0.125), (1.000, 0.140),  # 外壁(下→上)
        (1.055, 0.150), (1.055, 0.163),                                  # 縁の張り出し
        (0.930, 0.163), (0.880, 0.150),                                  # 縁の上面→内側へ
        (0.840, 0.060),                                                  # 内壁を底まで
    ]
    rings = [oval_ring(POT_A, POT_B, f, Z0 + dz) for f, dz in profile]
    loft(bm, rings, cap_start=True, cap_end=True)
    # 鉢の脚 4個(楕円に沿って配置)
    for deg in (40, 140, 220, 320):
        rad = math.radians(deg)
        px = POT_A * 0.66 * math.cos(rad)
        py = POT_B * 0.66 * math.sin(rad)
        add_box(bm, (px, py, Z0 + 0.0125), (0.065, 0.05, 0.025), taper=0.85)
    return finalize("BonsaiPot", bm, pot_color, smooth=True)

# ---------------------------------------------------------------- soil(土と苔)

SOIL = srgb("3A2C20")
MOSS = srgb("55703C")
MOSS2 = srgb("6B854A")

def soil_color(co, normal):
    n = value_noise(co, 14.0)
    up = max(normal.z, 0.0)
    moss_w = max(0.0, min(1.0, (n - 0.42) * 3.0)) * up
    n2 = value_noise(co + Vector((7.7, 3.3, 1.1)), 30.0)
    moss = tuple(MOSS[i] + (MOSS2[i] - MOSS[i]) * n2 for i in range(3))
    base = tuple(SOIL[i] + (moss[i] - SOIL[i]) * moss_w for i in range(3))
    return shade(base, normal, 0.92)


def build_soil():
    bm = bmesh.new()
    coarse = [
        (0.860, 0.145), (0.820, 0.162), (0.650, 0.178),
        (0.400, 0.188), (0.150, 0.194), (0.020, 0.196),
    ]
    # ノイズのスジ模様を防ぐため、プロファイルを細かく補間してから回す
    profile = []
    for i in range(len(coarse) - 1):
        f0, z0 = coarse[i]
        f1, z1 = coarse[i + 1]
        for k in range(4):
            t = k / 4.0
            profile.append((f0 + (f1 - f0) * t, z0 + (z1 - z0) * t))
    profile.append(coarse[-1])
    rings = [oval_ring(POT_A, POT_B, f, Z0 + dz) for f, dz in profile]
    loft(bm, rings, cap_start=True, cap_end=True)
    return finalize("BonsaiSoil", bm, soil_color, smooth=True)

# ---------------------------------------------------------------- rocks(岩)

ROCK = srgb("8A857C")

def rock_color(co, normal):
    n = value_noise(co, 40.0)
    base = tuple(c * (0.85 + 0.3 * n) for c in ROCK)
    return shade(base, normal)


def build_rock(name, center, radius, seed):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=2, radius=radius)
    for v in bm.verts:
        d = value_noise(v.co * (1.0 / max(radius, 1e-4)) + Vector((seed, seed, seed)), 2.5)
        v.co *= 0.78 + 0.45 * d
        v.co.z *= 0.72  # 平たくして据わりを良くする
    bmesh.ops.translate(bm, verts=bm.verts, vec=Vector(center))
    return finalize(name, bm, rock_color, smooth=True)

# ---------------------------------------------------------------- build & export

bpy.ops.wm.read_factory_settings(use_empty=True)

objects = [
    build_stand(),
    build_pot(),
    build_soil(),
    build_rock("BonsaiRock1", (0.145, 0.055, Z0 + 0.180), 0.045, 3.0),
    build_rock("BonsaiRock2", (-0.105, -0.060, Z0 + 0.176), 0.030, 8.0),
]

anchor = bpy.data.objects.new("TreeAnchor", None)
anchor.location = (0.0, 0.0, Z0 + 0.192)
bpy.context.scene.collection.objects.link(anchor)

root = bpy.data.objects.new("BonsaiBase", None)
bpy.context.scene.collection.objects.link(root)
for o in objects + [anchor]:
    o.parent = root

bpy.ops.export_scene.fbx(
    filepath=FBX_PATH,
    use_selection=False,
    object_types={'MESH', 'EMPTY'},
    apply_scale_options='FBX_SCALE_ALL',
    # 軸変換(Z-up→Y-up)をノードの回転ではなくデータ側に焼き込む。これをしないと Unity 側で
    # ルートに -90° 回転が乗り、回転をリセットする消費側コードでモデルが倒れてしまう。
    bake_space_transform=True,
    add_leaf_bones=False,
    # Unity はリニアカラースペース + Unlit シェーダーで頂点カラーを生値のまま出力するため、
    # sRGB エンコードで格納すると表示時に二重ガンマ補正となり白っぽく飛ぶ。リニアで格納する。
    colors_type='LINEAR',
)

# ------- プレビュー描画(Workbench / FLAT ライティング = Unity Unlit 相当)
cam_data = bpy.data.cameras.new("Cam")
cam = bpy.data.objects.new("Cam", cam_data)
bpy.context.scene.collection.objects.link(cam)
cam.location = (1.05, -1.15, 0.72)
look = Vector((0.0, 0.0, 0.28)) - cam.location
cam.rotation_euler = look.to_track_quat('-Z', 'Y').to_euler()
bpy.context.scene.camera = cam

scene = bpy.context.scene
scene.view_settings.view_transform = 'Standard'
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'FLAT'
scene.display.shading.color_type = 'VERTEX'
scene.render.resolution_x = 1000
scene.render.resolution_y = 900
scene.render.filepath = RENDER_PATH
bpy.ops.render.render(write_still=True)

bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
print("BONSAI_BASE_DONE fbx=%s" % FBX_PATH)
