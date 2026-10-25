# Il2CppInterop — patched-runtime layout support

让 [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) 能识别「Unity 版本号没变、但内部结构体被改过」的 il2cpp 运行时。

首个验证目标：《明日方舟：终末地》(Endfield) Beta —— Unity 2021.3.34f5 / metadata v29，但 `GameAssembly.dll` 里的 `Il2CppClass` 和 `Il2CppMethodInfo` 比原版多了几个字节。上游按版本号选结构体定义，于是全部错位。

> 补丁基于上游 `f03c8f4`（`1.5.1-ci.845+f03c8f4`，即 MelonLoader 0.7.3 自带的那份）。

---

## 症状

MelonLoader 0.7.3 + UnityExplorer 4.13.6，日志里出现：

```
[WARNING] [Il2CppInterop] Class::Init signatures have been exhausted, using a substitute!

[ERROR] [UnityExplorer] System.NullReferenceException
   at UniverseLib.Il2CppReflection.Initialize()
[ERROR] [Il2CppInterop] During invoking native->managed trampoline
System.NullReferenceException
   at MelonLoader.Support.SM_Component.Create()
```

注入的 MonoBehaviour 拿不到 `AddComponent` 的实例，`UniversalBehaviour.Instance` 是 null，UniverseLib 初始化直接中断，UnityExplorer 起不来。

## 根因

反汇编 `GameAssembly.dll` 得到的真实偏移（`python tools/verify_offsets.py` 可复现）：

| 字段 | 原版 | 本游戏 | 依据（导出函数里的指令） |
|---|---|---|---|
| `Il2CppClass.flags` | `0x114` | `0x114` | `il2cpp_class_get_flags`: `mov eax,[rcx+0x114]` |
| `Il2CppClass.token` | `0x118` | `0x118` | — |
| **插入点** | — | **+2 字节** | — |
| `Il2CppClass.method_count` | `0x11C` | `0x11E` | `il2cpp_class_get_methods` 的迭代 |
| `Il2CppClass.rank` | `0x12E` | `0x130` | `il2cpp_class_get_rank`: `movzx eax,byte [rcx+0x130]` |
| `Il2CppClass.typeHierarchyDepth` | `0x12C` | `0x12E` | `il2cpp_class_is_subclass_of` 读 `[..+0x12e]` |
| `Il2CppClass.bitfield0` | `0x132` | `0x134` | `il2cpp_class_has_references` 读 `[rcx+0x134]` bit5 |
| `Il2CppClass.bitfield1` | `0x133` | `0x135` | 同上，`test byte [rcx+0x135],1` = `size_inited` |
| `Il2CppMethodInfo.parameters_count` | `0x52` | `0x53` | `il2cpp_method_get_param_count`: `movzx eax,byte [rcx+0x53]` |
| `Il2CppMethodInfo.bitfield0` | `0x53` | `0x54` | `il2cpp_method_is_generic` 读 `[rcx+0x54]` bit0 |

`flags` / `token` / `instance_size` / `typeHierarchy` 都在插入点之前，保持原样；`Il2CppImage`、`Il2CppFieldInfo`、`Il2CppType` 也核对过，与原版一致。

错位之后 `ClassInjector` 把 `method_count` / `vtable_count` / `typeHierarchyDepth` 写进了错误的槽位，引擎侧读回 0 —— 于是 `AddComponent` 返回 null。

另外 `Class::Init` 的两条上游字节签名在这个版本上全部失配。真实地址可以从 `il2cpp_class_has_references` 反推：它的函数体就是
`if (!klass->size_inited) Class::Init(klass); return klass->has_references;`，所以体内第一条 `call` 就是 `Class::Init`。

## 补丁内容

5 个文件（3 新增 / 2 修改），见 `il2cppinterop-endfield.patch` 或 `src/` 下同名路径：

| 文件 | 作用 |
|---|---|
| `Runtime/LayoutProbe.cs`（新） | 启动时用 `il2cpp_class_get_methods` / `il2cpp_method_get_param_count` 返回的**真值**反推 padding，不信任版本号字符串 |
| `Runtime/VersionSpecific/Class/Class_29_0_Padded.cs`（新） | `token` 后多 2 字节的 `Il2CppClass` |
| `Runtime/VersionSpecific/MethodInfo/MethodInfo_29_0_Padded.cs`（新） | `slot` 后多 1 字节的 `Il2CppMethodInfo` |
| `Runtime/UnityVersionHandler.cs`（改） | `RecalculateHandlers()` 末尾调 `ApplyDetectedLayoutOverrides()`，按探测结果换入 padded handler |
| `Injection/InjectorHelpers.cs`（改） | `FindClassInit()` 增加 `FindClassInitFromExport()`，签名失配时以 `il2cpp_class_has_references` 为锚点找 `Class::Init` |

两个 padded handler **故意不加** `[ApplicableToUnityVersionsSince]`，避免和原版 `29_0` 抢版本区间；只在探针命中时手动换入，所以对正常游戏零影响。

两个结构体加了字段后对齐大小不变（`0x138` / `0x58`），vtable 位置和分配大小都不受影响。

## 用法

```bash
git clone https://github.com/BepInEx/Il2CppInterop.git
cd Il2CppInterop
git checkout f03c8f4
git apply /path/to/il2cppinterop-endfield.patch

dotnet build Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj -c Release
```

产物 `bin/Il2CppInterop.Runtime/net6.0/Il2CppInterop.Runtime.dll` 覆盖到：

```
<Game>/MelonLoader/net6/Il2CppInterop.Runtime.dll
```

（先备份原文件。程序集版本仍是 `1.5.1.0`，MelonLoader 的 SupportModule 不需要改。）

## 验证

```bash
pip install pefile capstone
python tools/verify_offsets.py "C:/path/to/GameAssembly.dll"
```

```
field                                       stock   found  delta
----------------------------------------------------------------
Il2CppClass.flags                           0x114   0x114     +0
Il2CppClass.rank                            0x12e   0x130     +2   <-- differs
Il2CppClass.bitfield1 (size_inited)         0x133   0x135     +2   <-- differs
Il2CppMethodInfo.parameters_count            0x52    0x53     +1   <-- differs
Il2CppMethodInfo.bitfield0                   0x53    0x54     +1   <-- differs

verdict:
  Il2CppClass      extra bytes after `token` : [2]
  Il2CppMethodInfo extra bytes after `slot`  : [1]
```

装好之后 MelonLoader 日志应变成：

```
[Il2CppInterop] il2cpp layout probe: Il2CppClass +2 byte(s), Il2CppMethodInfo +1 byte(s)
[UnityExplorer] UniverseLib 1.6.2 initialized.
[UnityExplorer] UnityExplorer 4.13.6 (IL2CPP) initialized.
```

不再有 `Class::Init signatures have been exhausted`，也不再出现 `SM_Component.Create()` 的 NRE。

## 兼容性

- 探针在每次启动时运行，stock 布局下探测结果为 `0 / 0`，行为与上游完全一致；
- 只对 metadata v29（Unity 2021.2+）有意义 —— 更老的版本选中的 handler 没有 padded 变体，命中时会打一条 warning 并保持原样；
- 游戏更新后重新跑一次 `verify_offsets.py`，如果 delta 变了，照着改两个 padded struct 即可（字段名和注释里都标了插入点）。

## 已知限制

- 只处理 `Il2CppClass` 与 `Il2CppMethodInfo` 两处；`Il2CppImage` / `Il2CppFieldInfo` / `Il2CppType` / `Il2CppParameterInfo` 已核对与本游戏一致，未做改动；
- `LayoutProbe` 依赖 `il2cpp_class_get_methods` 的返回值和 `method_count` 做比对，如果某个游戏把这两个 API 都改了，探针会退回「按原版布局处理」并打 warning；
- 不包含任何游戏文件、不包含反编译产物。

## 许可

补丁衍生自 [BepInEx/Il2CppInterop](https://github.com/BepInEx/Il2CppInterop)（LGPL-3.0），同许可发布，见 `LICENSE`。
