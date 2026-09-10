import math
from pathlib import Path

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
OUT_BLEND = ROOT / "Assets" / "Models" / "Robot.blend"
OUT_ROBOT_FBX = ROOT / "Assets" / "Models" / "RobotPlayer.fbx"
OUT_ARMS_FBX = ROOT / "Assets" / "Models" / "RobotArms.fbx"

SHARP_ANGLE = math.radians(40.0)


def hex_to_linear(value):
    value = value.lstrip("#")
    rgb = [int(value[i : i + 2], 16) / 255.0 for i in (0, 2, 4)]

    def convert(channel):
        return channel / 12.92 if channel <= 0.04045 else ((channel + 0.055) / 1.055) ** 2.4

    return tuple(convert(channel) for channel in rgb) + (1.0,)


COLORS = {
    "Cream": hex_to_linear("#F4F1E8"),
    "Orange": hex_to_linear("#D9553A"),
    "Mint": hex_to_linear("#9FD8B4"),
    "Dark": hex_to_linear("#26262B"),
    "Yellow": hex_to_linear("#E8B23A"),
    "Eye": hex_to_linear("#4FF5F0"),
}


def get_material(name):
    mat = bpy.data.materials.get(name)
    if mat is not None:
        return mat
    color = COLORS[name]
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = color
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = 0.6 if name == "Dark" else 0.45
    bsdf.inputs["Metallic"].default_value = 0.0
    if name == "Eye":
        bsdf.inputs["Emission Color"].default_value = color
        bsdf.inputs["Emission Strength"].default_value = 2.0
    return mat


class Builder:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.bm.loops.layers.uv.new("UVMap")
        self.part_layer = self.bm.verts.layers.int.new("part")
        self.materials = []
        self.parts = []
        self.current_part = None

    def slot(self, name):
        if name not in self.materials:
            self.materials.append(name)
        return self.materials.index(name)

    def part(self, name):
        self.current_part = name
        if name not in self.parts:
            self.parts.append(name)

    def tag(self, verts):
        if self.current_part is None:
            return
        index = self.parts.index(self.current_part) + 1
        for vert in verts:
            vert[self.part_layer] = index


def _capture(bm, make):
    faces = set(bm.faces)
    verts = set(bm.verts)
    make()
    new_faces = [f for f in bm.faces if f not in faces]
    new_verts = [v for v in bm.verts if v not in verts]
    return new_faces, new_verts


def _bevel(bm, verts, offset, segments):
    if offset <= 0:
        return
    edges = {e for v in verts for e in v.link_edges}
    bmesh.ops.bevel(
        bm,
        geom=list(edges),
        offset=offset,
        segments=segments,
        profile=0.5,
        affect="EDGES",
        clamp_overlap=True,
        loop_slide=True,
    )


def _orient(location, rotation):
    return Matrix.Translation(Vector(location)) @ Euler(rotation, "XYZ").to_matrix().to_4x4()


def _assign(builder, faces, verts, material):
    for face in faces:
        face.material_index = builder.slot(material)
    builder.tag(verts)


def add_box(builder, material, size, location, rotation=(0.0, 0.0, 0.0), bevel=0.03, segments=2):
    bm = builder.bm

    def make():
        res = bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=Vector(size), verts=res["verts"])
        _bevel(bm, res["verts"], bevel, segments)

    faces, verts = _capture(bm, make)
    _assign(builder, faces, verts, material)
    bmesh.ops.transform(bm, matrix=_orient(location, rotation), verts=verts)


def add_box_between(builder, material, start, end, width, height, bevel=0.02, segments=2, pad=0.0):
    start = Vector(start)
    end = Vector(end)
    offset = end - start
    length = offset.length + pad
    if length < 1e-5:
        return
    bm = builder.bm
    rotation = Vector((0.0, 0.0, 1.0)).rotation_difference(offset.normalized()).to_matrix().to_4x4()

    def make():
        res = bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=Vector((width, height, length)), verts=res["verts"])
        _bevel(bm, res["verts"], bevel, segments)

    faces, verts = _capture(bm, make)
    _assign(builder, faces, verts, material)
    matrix = Matrix.Translation((start + end) / 2.0) @ rotation
    bmesh.ops.transform(bm, matrix=matrix, verts=verts)


def add_cylinder(builder, material, radius, depth, location, rotation=(0.0, 0.0, 0.0), segments=20, cap=True):
    bm = builder.bm

    def make():
        bmesh.ops.create_cone(
            bm,
            cap_ends=cap,
            cap_tris=False,
            segments=segments,
            radius1=radius,
            radius2=radius,
            depth=depth,
            calc_uvs=True,
        )

    faces, verts = _capture(bm, make)
    _assign(builder, faces, verts, material)
    bmesh.ops.transform(bm, matrix=_orient(location, rotation), verts=verts)


def add_cylinder_along(builder, material, radius, depth, center, direction, segments=20):
    rotation = Vector((0.0, 0.0, 1.0)).rotation_difference(Vector(direction).normalized()).to_euler()
    add_cylinder(builder, material, radius, depth, center, rotation, segments)


def add_sphere(builder, material, radius, location, segments=20, rings=12, scale=(1.0, 1.0, 1.0)):
    bm = builder.bm

    def make():
        res = bmesh.ops.create_uvsphere(
            bm, u_segments=segments, v_segments=rings, radius=radius, calc_uvs=True
        )
        bmesh.ops.scale(bm, vec=Vector(scale), verts=res["verts"])

    faces, verts = _capture(bm, make)
    _assign(builder, faces, verts, material)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(location)), verts=verts)


def finish(builder, origin=(0.0, 0.0, 0.0)):
    bm = builder.bm
    for face in bm.faces:
        face.smooth = True
    bm.normal_update()
    for edge in bm.edges:
        if len(edge.link_faces) == 2 and edge.calc_face_angle() > SHARP_ANGLE:
            edge.smooth = False
    mesh = bpy.data.meshes.new(builder.name)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(builder.name, mesh)
    for name in builder.materials:
        obj.data.materials.append(get_material(name))
    bpy.context.scene.collection.objects.link(obj)
    origin = Vector(origin)
    if origin.length > 1e-6:
        mesh.transform(Matrix.Translation(-origin))
        obj.location = origin
    if builder.parts:
        values = [0] * len(mesh.vertices)
        attribute = mesh.attributes.get("part")
        if attribute is not None:
            for index in range(len(mesh.vertices)):
                values[index] = attribute.data[index].value
            mesh.attributes.remove(attribute)
        for part_index, part_name in enumerate(builder.parts, start=1):
            group = obj.vertex_groups.new(name=part_name)
            ids = [index for index, value in enumerate(values) if value == part_index]
            if ids:
                group.add(ids, 1.0, "REPLACE")
    return obj


def lerp(a, b, t):
    return Vector(a).lerp(Vector(b), t)


def robot_arm_points(side):
    pivot = Vector((side * 0.30, 0.0, 0.78))
    elbow = pivot + Vector((side * 0.07, -0.02, -0.22))
    wrist = pivot + Vector((side * 0.12, -0.22, -0.36))
    return pivot, elbow, wrist


def fps_arm_points(side):
    pivot = Vector((side * 0.46, -0.10, -0.38))
    elbow = Vector((side * 0.58, -0.42, -0.44))
    wrist = Vector((side * 0.46, -0.80, -0.26))
    return pivot, elbow, wrist


def build_body():
    builder = Builder("Robot_Body")
    builder.part("Hips")
    add_box(builder, "Cream", (0.40, 0.34, 0.18), (0, 0, 0.36), bevel=0.05, segments=3)
    builder.part("Torso")
    add_box(builder, "Orange", (0.46, 0.38, 0.12), (0, 0, 0.50), bevel=0.04, segments=3)
    add_box(builder, "Cream", (0.52, 0.42, 0.26), (0, 0, 0.68), bevel=0.06, segments=3)
    add_box(builder, "Mint", (0.18, 0.04, 0.11), (0, -0.215, 0.70), bevel=0.015, segments=2)
    for side in (-1.0, 1.0):
        add_sphere(builder, "Mint", 0.085, (side * 0.30, 0, 0.78))
    builder.part("Head")
    add_cylinder(builder, "Dark", 0.10, 0.09, (0, 0, 0.86), segments=20)
    add_box(builder, "Cream", (0.50, 0.44, 0.40), (0, 0, 1.06), bevel=0.10, segments=3)
    add_box(builder, "Dark", (0.38, 0.05, 0.24), (0, -0.21, 1.07), bevel=0.03, segments=3)
    for side in (-1.0, 1.0):
        add_cylinder(builder, "Mint", 0.08, 0.05, (side * 0.265, 0, 1.07), (0, math.radians(90), 0), segments=16)
        add_cylinder(builder, "Dark", 0.036, 0.07, (side * 0.275, 0, 1.07), (0, math.radians(90), 0), segments=12)
    return finish(builder)


def build_eyes():
    origin = Vector((0.0, -0.238, 1.07))
    builder = Builder("Robot_Eyes")
    builder.part("Eyes")
    for cx in (-0.10, 0.10):
        apex = Vector((cx, 0.0, 0.018))
        for direction in (Vector((-0.85, 0.0, -0.53)), Vector((0.85, 0.0, -0.53))):
            center = apex + direction * 0.041
            rotation = (0.0, -math.atan2(direction.z, direction.x), 0.0)
            add_box(builder, "Eye", (0.085, 0.022, 0.016), origin + center, rotation, bevel=0.004, segments=2)
    return finish(builder, origin)


def build_antenna():
    builder = Builder("Robot_Antenna")
    builder.part("Antenna")
    base = Vector((-0.14, 0.0, 1.24))
    add_cylinder(builder, "Dark", 0.013, 0.30, base + Vector((0, 0, 0.15)), segments=12)
    add_sphere(builder, "Orange", 0.038, base + Vector((0, 0, 0.31)), segments=16, rings=10)
    return finish(builder, base)


def build_wheel(side):
    x = side * 0.30
    suffix = "R" if side > 0 else "L"
    builder = Builder(f"Robot_Wheel_{suffix}")
    builder.part(f"Wheel_{suffix}")
    rotation = (0.0, math.radians(90), 0.0)
    add_cylinder(builder, "Dark", 0.24, 0.10, (x, 0, 0.24), rotation, segments=24)
    add_cylinder(builder, "Mint", 0.155, 0.13, (x, 0, 0.24), rotation, segments=24)
    add_cylinder(builder, "Dark", 0.055, 0.145, (x, 0, 0.24), rotation, segments=16)
    add_cylinder(builder, "Cream", 0.09, 0.155, (x, 0, 0.24), rotation, segments=16)
    add_cylinder(builder, "Dark", 0.035, 0.165, (x, 0, 0.24), rotation, segments=12)
    return finish(builder, (x, 0, 0.24))


def build_robot_arm(side):
    suffix = "R" if side > 0 else "L"
    pivot, elbow, wrist = robot_arm_points(side)
    elbow = elbow - pivot
    wrist = wrist - pivot
    builder = Builder(f"Robot_Arm_{suffix}")
    builder.part(f"UpperArm_{suffix}")
    add_box_between(builder, "Cream", (0, 0, -0.02), elbow, 0.115, 0.115, bevel=0.03, segments=3)
    builder.part(f"Forearm_{suffix}")
    add_cylinder(builder, "Dark", 0.062, 0.14, elbow, (0, math.radians(90), 0), segments=16)
    add_box_between(builder, "Cream", elbow, wrist, 0.105, 0.105, bevel=0.028, segments=3)
    add_box_between(builder, "Mint", lerp(elbow, wrist, 0.04), lerp(elbow, wrist, 0.20), 0.118, 0.118, bevel=0.015, segments=2)
    direction = (wrist - elbow).normalized()
    add_cylinder_along(builder, "Dark", 0.05, 0.075, wrist, direction, segments=16)
    builder.part(f"Hand_{suffix}")
    hand_end = wrist + direction * 0.17
    add_box_between(builder, "Yellow", wrist + direction * 0.02, hand_end, 0.13, 0.13, bevel=0.04, segments=3)
    obj = finish(builder)
    obj.location = pivot
    return obj


def build_fps_arm(side):
    suffix = "R" if side > 0 else "L"
    pivot, elbow, wrist = fps_arm_points(side)
    builder = Builder(f"FpsArm_{suffix}")

    builder.part(f"UpperArm_{suffix}")
    add_sphere(builder, "Mint", 0.11, pivot, scale=(1.0, 1.15, 1.0))
    add_box_between(builder, "Cream", pivot + Vector((0, 0.02, 0)), elbow, 0.15, 0.15, bevel=0.04, segments=3)

    builder.part(f"Forearm_{suffix}")
    add_cylinder(builder, "Dark", 0.08, 0.19, elbow, (0, math.radians(90), 0), segments=20)
    add_box(builder, "Mint", (0.11, 0.12, 0.05), elbow + Vector((0, 0, 0.09)), bevel=0.02, segments=2)
    add_box(builder, "Orange", (0.11, 0.12, 0.05), elbow + Vector((0, 0, -0.09)), bevel=0.02, segments=2)

    add_box_between(builder, "Cream", elbow, wrist, 0.145, 0.145, bevel=0.04, segments=3)
    top_a = lerp(elbow, wrist, 0.50) + Vector((0, 0, 0.068))
    top_b = lerp(elbow, wrist, 0.72) + Vector((0, 0, 0.068))
    add_box_between(builder, "Mint", top_a, top_b, 0.135, 0.055, bevel=0.018, segments=2)
    band_a = lerp(elbow, wrist, 0.80)
    band_b = lerp(elbow, wrist, 0.95)
    add_box_between(builder, "Orange", band_a, band_b, 0.158, 0.158, bevel=0.018, segments=2)

    direction = (wrist - elbow).normalized()
    add_cylinder_along(builder, "Dark", 0.068, 0.085, wrist, direction, segments=16)

    builder.part(f"Hand_{suffix}")
    palm_start = wrist + direction * 0.02
    palm_end = wrist + direction * 0.17
    add_box_between(builder, "Dark", palm_start, palm_end, 0.15, 0.16, bevel=0.035, segments=3)

    finger_dir = (Matrix.Rotation(-0.16, 4, "X") @ direction).normalized()
    base = wrist + direction * 0.17
    for offset in (-0.047, 0.0, 0.047):
        root = base + Vector((offset * side, 0.0, 0.0))
        tip = root + finger_dir * 0.13
        add_box_between(builder, "Dark", root, tip, 0.042, 0.055, bevel=0.012, segments=2)
        add_sphere(builder, "Cream", 0.027, root, segments=12, rings=8)
        add_sphere(builder, "Dark", 0.024, tip, segments=12, rings=8)

    thumb_dir = (Matrix.Rotation(-0.12, 4, "X") @ (Matrix.Rotation(side * 1.0, 4, "Z") @ direction)).normalized()
    thumb_root = wrist + direction * 0.10 + Vector((-side * 0.08, 0.0, 0.01))
    add_box_between(builder, "Dark", thumb_root, thumb_root + thumb_dir * 0.11, 0.045, 0.055, bevel=0.012, segments=2)
    add_sphere(builder, "Cream", 0.027, thumb_root, segments=12, rings=8)

    return finish(builder, pivot)


def build_armature(name, bones):
    armature = bpy.data.armatures.new(name)
    arm_obj = bpy.data.objects.new(name, armature)
    bpy.context.scene.collection.objects.link(arm_obj)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = {}
    for bone_name, head, tail, parent in bones:
        bone = armature.edit_bones.new(bone_name)
        bone.head = Vector(head)
        bone.tail = Vector(tail)
        if parent is not None:
            bone.parent = edit_bones[parent]
        edit_bones[bone_name] = bone
    bpy.ops.object.mode_set(mode="OBJECT")
    return arm_obj


def robot_bones():
    bones = [
        ("Root", (0.0, 0.0, 0.0), (0.0, 0.0, 0.30), None),
        ("Hips", (0.0, 0.0, 0.36), (0.0, 0.0, 0.45), "Root"),
        ("Torso", (0.0, 0.0, 0.45), (0.0, 0.0, 0.81), "Hips"),
        ("Head", (0.0, 0.0, 0.81), (0.0, 0.0, 1.26), "Torso"),
        ("Eyes", (0.0, -0.238, 1.07), (0.0, -0.338, 1.07), "Head"),
        ("Antenna", (-0.14, 0.0, 1.24), (-0.14, 0.0, 1.54), "Head"),
    ]
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        pivot, elbow, wrist = robot_arm_points(side)
        hand = wrist + (wrist - elbow).normalized() * 0.17
        bones.append((f"Wheel_{suffix}", (side * 0.30, 0.0, 0.24), (side * 0.44, 0.0, 0.24), "Root"))
        bones.append((f"UpperArm_{suffix}", tuple(pivot), tuple(elbow), "Torso"))
        bones.append((f"Forearm_{suffix}", tuple(elbow), tuple(wrist), f"UpperArm_{suffix}"))
        bones.append((f"Hand_{suffix}", tuple(wrist), tuple(hand), f"Forearm_{suffix}"))
    return bones


def fps_bones():
    bones = [("Root", (0.0, 0.0, 0.0), (0.0, 0.0, 0.12), None)]
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        pivot, elbow, wrist = fps_arm_points(side)
        hand = wrist + (wrist - elbow).normalized() * 0.17
        bones.append((f"UpperArm_{suffix}", tuple(pivot), tuple(elbow), "Root"))
        bones.append((f"Forearm_{suffix}", tuple(elbow), tuple(wrist), f"UpperArm_{suffix}"))
        bones.append((f"Hand_{suffix}", tuple(wrist), tuple(hand), f"Forearm_{suffix}"))
    return bones


def bind(mesh_obj, arm_obj):
    mesh_obj.data.transform(mesh_obj.matrix_world)
    mesh_obj.matrix_world = Matrix.Identity(4)
    mesh_obj.parent = arm_obj
    modifier = mesh_obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = arm_obj


def export_fbx(objects, path):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=str(path),
        use_selection=True,
        add_leaf_bones=False,
        use_mesh_modifiers=False,
        bake_anim=False,
    )


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0

    robot_meshes = [
        build_body(),
        build_eyes(),
        build_antenna(),
        build_wheel(-1.0),
        build_wheel(1.0),
        build_robot_arm(-1.0),
        build_robot_arm(1.0),
    ]
    robot_rig = build_armature("RobotPlayer", robot_bones())
    for mesh_obj in robot_meshes:
        bind(mesh_obj, robot_rig)

    arm_meshes = [build_fps_arm(-1.0), build_fps_arm(1.0)]
    arms_rig = build_armature("RobotArms", fps_bones())
    for mesh_obj in arm_meshes:
        bind(mesh_obj, arms_rig)

    bpy.ops.wm.save_as_mainfile(filepath=str(OUT_BLEND), check_existing=False)
    export_fbx([robot_rig] + robot_meshes, OUT_ROBOT_FBX)
    export_fbx([arms_rig] + arm_meshes, OUT_ARMS_FBX)

    for mesh_obj in robot_meshes + arm_meshes:
        groups = [group.name for group in mesh_obj.vertex_groups]
        print(f"{mesh_obj.name}: {len(mesh_obj.data.vertices)} verts, {len(mesh_obj.data.polygons)} faces, groups={groups}")
    print(f"RobotPlayer bones: {[bone.name for bone in robot_rig.data.bones]}")
    print(f"RobotArms bones: {[bone.name for bone in arms_rig.data.bones]}")
    print(f"Saved: {OUT_BLEND}")
    print(f"Saved: {OUT_ROBOT_FBX}")
    print(f"Saved: {OUT_ARMS_FBX}")


if __name__ == "__main__":
    main()
