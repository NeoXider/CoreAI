# The Full tier: unity_* reflection, only when the host enabled Full Lua

Inspect first, edit second. Ids come from the find functions.
- `unity_list_objects(max)`, `unity_find(name)`, `unity_find_all(pattern,max)`,
  `unity_find_by_tag(tag,max)`, `unity_find_by_component(type,max)`
- `unity_describe_object(id)`, `unity_get_transform(id)`, `unity_get_children(id)`,
  `unity_list_components(id)`
- `unity_set_position(id,x,y,z)`, `unity_set_rotation_euler(id,x,y,z)`, `unity_set_scale(id,x,y,z)`,
  `unity_parent(childId, parentIdOr0, worldPositionStays)`
- `unity_get_member(id,component,member)`, `unity_set_member(id,component,member,value)`,
  `unity_call(id,component,method,argsTable)`
Prefer the Rbx surface (Part.Position / Part.CFrame) for plain transforms even in Full mode.
