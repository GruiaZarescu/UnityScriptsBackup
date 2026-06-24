using UnityEngine;

public class BlenderScriptForContext
{
    
}
/*
import os
import bpy


HIDE_GROUP_PREFIX = "Hide"
HIDE_UV_MAP_NAME = "HideMask"
FALLBACK_PRIMARY_UV_NAME = "UVMap"
REPORT_FILE_NAME = "HideMask_BitMapping.txt"
REPORT_TEXT_BLOCK_NAME = "HideMask_BitMapping"
MIN_GROUP_WEIGHT = 0.5
MAX_MASK_BITS_IN_UV_FLOAT = 24
MAKE_SINGLE_USER_MESHES = True
TARGET_UV_INDEX = 1  # Blender UV map index 1 imports as Unity mesh.uv2.


def main():
    report_lines = []
    report_lines.append("Hide Mask UV Bake Report")
    report_lines.append("========================")
    report_lines.append(f"Hide group prefix: {HIDE_GROUP_PREFIX}")
    report_lines.append(f"UV map name: {HIDE_UV_MAP_NAME}")
    report_lines.append(f"Target Blender UV index: {TARGET_UV_INDEX} (Unity uv2)")
    report_lines.append(f"Minimum vertex group weight: {MIN_GROUP_WEIGHT}")
    report_lines.append(f"Max exact bits in uv.x: {MAX_MASK_BITS_IN_UV_FLOAT}")
    report_lines.append("")

    processed_count = 0
    skipped_count = 0

    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue

        hide_groups = get_hide_groups(obj)
        if not hide_groups:
            skipped_count += 1
            continue

        processed_count += 1
        bake_object_hide_masks(obj, hide_groups, report_lines)

    report_lines.append("")
    report_lines.append("Summary")
    report_lines.append("-------")
    report_lines.append(f"Processed mesh objects: {processed_count}")
    report_lines.append(f"Skipped mesh objects without hide groups: {skipped_count}")

    report_text = "\n".join(report_lines)
    write_report(report_text)
    print(report_text)


def get_hide_groups(obj):
    return [group for group in obj.vertex_groups if group.name.startswith(HIDE_GROUP_PREFIX)]


def bake_object_hide_masks(obj, hide_groups, report_lines):
    if len(hide_groups) > MAX_MASK_BITS_IN_UV_FLOAT:
        used_groups = hide_groups[:MAX_MASK_BITS_IN_UV_FLOAT]
        ignored_groups = hide_groups[MAX_MASK_BITS_IN_UV_FLOAT:]
    else:
        used_groups = hide_groups
        ignored_groups = []

    if MAKE_SINGLE_USER_MESHES and obj.data.users > 1:
        old_mesh_name = obj.data.name
        obj.data = obj.data.copy()
        obj.data.name = f"{old_mesh_name}_{obj.name}_HideMask"

    mesh = obj.data
    hide_uv_layer = ensure_hide_uv_layer(obj)
    group_index_to_bit = {group.index: bit for bit, group in enumerate(used_groups)}
    vertex_masks = build_vertex_masks(mesh, group_index_to_bit)

    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            vertex_index = mesh.loops[loop_index].vertex_index
            hide_uv_layer.data[loop_index].uv = (float(vertex_masks[vertex_index]), 0.0)

    mesh.update()

    report_object_mapping(obj, mesh, hide_uv_layer, used_groups, ignored_groups, vertex_masks, report_lines)


def ensure_hide_uv_layer(obj):
    mesh = obj.data

    if len(mesh.uv_layers) == 0:
        mesh.uv_layers.new(name=FALLBACK_PRIMARY_UV_NAME)

    if HIDE_UV_MAP_NAME not in mesh.uv_layers:
        mesh.uv_layers.new(name=HIDE_UV_MAP_NAME)

    move_uv_layer_to_target_index(obj, HIDE_UV_MAP_NAME, TARGET_UV_INDEX)

    # Re-fetch AFTER moving
    hide_uv_layer = mesh.uv_layers.get(HIDE_UV_MAP_NAME)

    mesh.update()

    return hide_uv_layer


def move_uv_layer_to_target_index(obj, layer_name, target_index):
    mesh = obj.data
    current_index = get_uv_layer_index(mesh, layer_name)
    if current_index == -1 or current_index == target_index:
        return

    try:
        mesh.uv_layers.move(current_index, target_index)
        return
    except Exception:
        pass

    try_move_uv_layer_with_operator(obj, layer_name, target_index)


def try_move_uv_layer_with_operator(obj, layer_name, target_index):
    mesh = obj.data
    previous_active = bpy.context.view_layer.objects.active
    previous_selection = list(bpy.context.selected_objects)
    previous_mode = obj.mode if bpy.context.view_layer.objects.active == obj else "OBJECT"

    try:
        if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")

        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj

        while True:
            current_index = get_uv_layer_index(mesh, layer_name)
            if current_index == -1 or current_index == target_index:
                break

            mesh.uv_layers.active_index = current_index
            direction = "UP" if current_index > target_index else "DOWN"
            bpy.ops.mesh.uv_texture_move(direction=direction)
    except Exception as ex:
        print(f"Warning: Could not move UV map '{layer_name}' on '{obj.name}' to index {target_index}: {ex}")
    finally:
        bpy.ops.object.select_all(action="DESELECT")
        for selected_obj in previous_selection:
            if selected_obj.name in bpy.data.objects:
                selected_obj.select_set(True)

        if previous_active is not None and previous_active.name in bpy.data.objects:
            bpy.context.view_layer.objects.active = previous_active

        if previous_active == obj and previous_mode != "OBJECT":
            try:
                bpy.ops.object.mode_set(mode=previous_mode)
            except Exception:
                pass


def get_uv_layer_index(mesh, layer_name):
    for index, layer in enumerate(mesh.uv_layers):
        if layer.name == layer_name:
            return index

    return -1


def build_vertex_masks(mesh, group_index_to_bit):
    vertex_masks = [0] * len(mesh.vertices)

    for vertex in mesh.vertices:
        mask = 0
        for membership in vertex.groups:
            if membership.weight < MIN_GROUP_WEIGHT:
                continue

            if membership.group not in group_index_to_bit:
                continue

            mask |= 1 << group_index_to_bit[membership.group]

        vertex_masks[vertex.index] = mask

    return vertex_masks


def report_object_mapping(obj, mesh, hide_uv_layer, used_groups, ignored_groups, vertex_masks, report_lines):
    uv_index = get_uv_layer_index(mesh, hide_uv_layer.name)
    report_lines.append(f"Object: {obj.name}")
    report_lines.append(f"Mesh: {mesh.name}")
    report_lines.append(f"Hide UV map: {hide_uv_layer.name}")
    report_lines.append(f"Blender UV index: {uv_index}")

    if uv_index == TARGET_UV_INDEX:
        report_lines.append("Unity channel: uv2")
    else:
        report_lines.append(f"WARNING: Expected UV index {TARGET_UV_INDEX} for Unity uv2, but got index {uv_index}.")

    for bit, group in enumerate(used_groups):
        vertex_count = count_vertices_with_bit(vertex_masks, bit)
        report_lines.append(f"  bit {bit:02d}: {group.name} ({vertex_count} verts)")

    for group in ignored_groups:
        report_lines.append(f"  IGNORED: {group.name} (over {MAX_MASK_BITS_IN_UV_FLOAT} bit uv.x limit)")

    report_lines.append("")


def count_vertices_with_bit(vertex_masks, bit):
    bit_value = 1 << bit
    return sum(1 for mask in vertex_masks if (mask & bit_value) != 0)


def write_report(report_text):
    text_block = bpy.data.texts.get(REPORT_TEXT_BLOCK_NAME)
    if text_block is None:
        text_block = bpy.data.texts.new(REPORT_TEXT_BLOCK_NAME)

    text_block.clear()
    text_block.write(report_text)

    report_path = get_report_path()
    with open(report_path, "w", encoding="utf-8") as report_file:
        report_file.write(report_text)

    print(f"Hide mask report written to: {report_path}")


def get_report_path():
    if bpy.data.filepath:
        directory = os.path.dirname(bpy.data.filepath)
    else:
        directory = os.getcwd()

    return os.path.join(directory, REPORT_FILE_NAME)


if __name__ == "__main__":
    main()
*/
