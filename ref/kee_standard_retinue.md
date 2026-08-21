# 标准卫队外观：EE 素材清单（已核实存在，尚未验证可用）

提取方式（可复现，一条命令）：`Bundles/cheatdata.json` 按 `TypeFullName` 过滤
`Kingmaker.Visual.CharacterSystem.KingmakerEquipmentEntity`（全库 620 条）。
cheatdata 只有 `Name / Guid / TypeFullName`，是权威索引，不含组件数据。

## 两种 id 不能混用

- 下表是 **`KingmakerEquipmentEntity` 蓝图 guid**。
- `DollData.EquipmentEntityIds` 要的是 **EE 资源 AssetId**，来自
  `EquipmentEntityLink.AssetId`（`DollState.EEAdapter.AssetId => m_Link.AssetId`）。

转换在运行时做，不用碰 bbp：

```csharp
var kee = ResourcesLibrary.TryGetBlueprint(guid) as KingmakerEquipmentEntity;
// public EquipmentEntityLink[] m_MaleArray / m_FemaleArray  —— 都是 public 字段
// 注意 m_RaceDependent / m_RaceDependentArrays[8]：种族相关的要按种族取
foreach (var link in kee.m_MaleArray) ids.Add(link.AssetId);
```

## 套件（对应 Nexus images/29 那张截图）

| 部位 | KEE 蓝图名 | guid |
|---|---|---|
| 护甲 | `KEE_ArmorKasrkin1` | `a777e0dba135405998b713aaf4bb67a4` |
| 护甲(备) | `KEE_ArmorKasrkin2` | `4055c4ac3c0247afaa75b1cf6ee3216a` |
| 头盔 | `KEE_HelmetKasrkin1` | `f344c7545656456aaa52ea4bd85bb214` |
| 头盔(备) | `KEE_HelmetKasrkin2` | `e76890ab0bad4b54be85010246701734` |
| 靴 | `KEE_BootsKasrkin` | `650f2260c8634abeb4d74c28c15c884a` |
| 手套 | `KEE_GlovesKasrkinArmor` | `7b79c080d9ac4bb7afbe87f2f9c3e1d9` |
| 背包 | `KEE_HellGunEquipmentEntity` | `9e28bfffe6114efea852b1cdcc2d8f59` |
| 基础外套 | `KEE_OccupationAstraMilitarumClothes` | `394e4b94f1284fefa4f47f1df0e42161` |
| 腰带 | `KEE_Randomizer_BeltArmorVoidsmen` | `193dcc8d662e41a9a7093c01a2381244` |
| 肩甲 | `KEE_SpauldersAdeptusAdministratum` | `9a94c9006d124a11a0a28d92016c2023` |
| 头盔配件 | `KEE_HelmetAstraMilitarumRogueTrader` | `5451c93ce0fd43bdae7f72de8e9581f4` |

同名系列还有更多可选：Kasrkin 6 件、Militarum 11 件、Voidsman 5 件、ChaosCultist 45 件、
Administratum 6 件。要换配色/款式在这些里挑。

## 人类 RaceVisualPreset（全库 34 条，RT 只用得上这几条）

| 名 | guid |
|---|---|
| `Human_Standard_VisualPreset` | `58181bf151eb0c0408f82546541dcc03` |
| `Human_Standard_VisualPreset_fat` | `10d74847c1492bf428b462f948e69d4f` |
| `Human_Standard_VisualPreset_thin` | `e03b9c63971878743b8f53bdf14673ee` |
| `Human_Tall_VisualPreset` | `3a691e2c33c914f4c8ea2be45515ba38` |
| `Eldar_Standard_VisualPreset` | `00491245397b457aafc0ee215d1ee8ec` |

其余 Aasimar / Dwarf / Elf / Gnome / Halfling / HalfOrc / Tiefling 是 Owlcat 从开拓者
共用代码库带过来的残留，RT 用不到。

## 还没验证的前提

**素材齐全 ≠ 这条路能走。** 决定性的一条是卫兵单位的模型能不能被拼装：

- 1.0.68 实测：83 个候选单位里 11 个无 `Character`，72 个有 `Character` 但
  `EquipmentEntitiesForPreload` **全部 0 件**。
- 0 件强烈提示这些 prefab 是 `BakedCharacter`（整套网格提前合批烘死）。
  `UnitEntityView.UpdateBodyEquipmentModel` 第一句就是
  `if (CharacterAvatar == null || (bool)CharacterAvatar.BakedCharacter) return;`
  —— 烘焙过的连**装备护甲的视觉都不生效**，往上加 EE 自然也不渲染。
- 1.0.69 的探测加了这一档判定，结论以那次为准。

若确认全是烘焙的，唯一出路是 `DollData`：`CreateUnitView()` 拿
`BlueprintRoot.Instance.CharGenRoot.MaleDoll/FemaleDoll` 做底座重新拼，那个底座
是全游戏唯一确定没烘焙的 `Character`。代价见下。

## doll 路线的三个代价

1. **只能是人形** —— 底座是 chargen 人类骨架，阿斯塔特不可能。
2. **进存档、进哈希** —— `DollData` 是 `[JsonProperty]`，且
   `PartUnitViewSettings.GetHash128()` 里 `ClassHasher<DollData>.GetHash128(Doll)`。
   而现有的 `appearanceUnit`（patch `get_PrefabGuid`）**不进哈希**，只有
   `m_CustomPrefabGuid` 进。所以换 doll = 新增一个联机失步源，开关必须双方一致。
3. **护甲视觉会盖上来** —— `BlueprintArmorType.m_EquipmentEntity`。

第 3 条有干净解法：装备视觉的两个应用点
（`UpdateBodyEquipmentModel` 与 `HandleEquipmentSlotUpdated`）**共用
`UnitEntityView.ExtractEquipmentEntities(slot)`**，挡这一个函数两条路一起挡。
`Character` 是纯渲染组件，不进存档也不进实体哈希，所以「关闭护甲外观」这个开关
可以是**纯本地偏好**，两边设置不同也不影响同步。
