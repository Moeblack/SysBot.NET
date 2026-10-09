# Showdown+ v1：PS 配置可选扩展

Showdown+ 在原生 Pokémon Showdown（PS）配置上增加少量可选字段，并将明确标注的 Pokémon Champions（冠军）SP 转换为朱紫努力值。**所有扩展都可以不填**：直接粘贴普通 PS 配置即可，不需要 `Version` 头，也不需要为每只补齐 OT、尺寸、奖章或相遇信息。

当前入口是本分支的本地网页/API 配置派送，目标是 **Scarlet/Violet（朱紫，PK9）**。这不是冠军游戏派送工具，也不是任意 PKHeX 属性编辑器。USB、本地交换与启动步骤见 [朱紫本地交换说明](SV-LOCAL-TRADE.zh-CN.md)。

## 最简使用与分隔

- 使用英文物种、招式、特性和 PS 字段名；保留原生 `@` 道具、性别、形态、`Ability:`、`Level:`、`Shiny:`、`EVs:`、`IVs:`、性格和 `- 招式` 写法。
- 每只之间留一个空行，或单独写一行 `---`；一次 1～12 只，文本最多 60000 字符。
- 可粘贴闭合的三反引号代码围栏；不要把一只的配置拆在多个围栏里。`#` 或 `//` 开头的整行可作注释；若该行本身是合法 PS 成员头（例如 `#Hero (Eevee)`），优先保留为昵称，不按注释删除。
- 支持 PS 队伍标题 `=== ... ===`。标题的方括号格式标识含 `champions`（例如 `=== [gen9champions] 队名 ===`，不区分大小写）时，随后进入冠军 SP 模式；其他队伍标题恢复标准 EV 模式。
- 格式指令建议写在一只配置之前，作用持续到下一次 `Format:` 或队伍标题；空行、`---`、代码围栏不会取消格式模式。遇到 `Format:` 会结束前一只的文本，再切换后续模式，不会追溯重解前面的 EV。

## 简便语法表

下表字段均可省略。字段名大小写不敏感；不要用中文标点替代 `:`、`=`、`,` 或 `/`。

| 写法 | 值与含义 |
| --- | --- |
| `Format: Showdown` | 标准模式（默认），`EVs:` 按传统 EV 解读。别名 `Standard`、`EV`、`SV`、`Gen3`～`Gen9` 也只选择传统 EV 单位，不选择派送世代。 |
| `Format: Champions` | 冠军模式，将随后每只的 `EVs:` 按 SP 解读。别名 `Champion`、`SP`。 |
| `Target: SV` | 当前派送目标；可写 `PK9`、`Gen9`、`Scarlet`、`Violet`。其他目标明确报不支持，不会静默转换成朱紫。 |
| `SPs: 32 HP / 32 Atk / 2 Spe` | 显式冠军 SP；`SP:` 也可。不需要 `Format:`。每项 0～32，总和不超过 66；未列出的项为 0。属性为 `HP`、`Atk`、`Def`、`SpA`、`SpD`、`Spe`。 |
| `OT: Alice` | 原训练家名字，1～12 个字符。 |
| `OTGender: Female` | 原训练家性别；支持 `Male`/`Female`、`M`/`F`、`0`/`1`、`男`/`女`。与宝可梦自身性别不同。 |
| `TID: 123456` | 第七世代起的公开 ID，0～999999。 |
| `SID: 1234` | 对应显示 ID 体系的隐藏 ID，0～4294；手填组合须满足 `SID × 1000000 + TID ≤ 4294967295`。不是直接填旧世代的 16 位 TID/SID。 |
| `Height: 128` | `HeightScalar`，0～255 的高度标量，**不是厘米或米**。 |
| `Weight: 128` | `WeightScalar`，0～255 的重量标量，**不是公斤**。 |
| `Scale: 128` 或 `Size: 128` | `Scale`，0～255 的尺寸标量；不保证所有遭遇都允许任意尺寸。 |
| `Marks: Mightiest, Partner` | 英文 PKHeX 属性后缀，分别对应 `RibbonMarkMightiest`、`RibbonMarkPartner`；也可写完整属性名。逗号分隔，不接受中文奖章名。 |
| `Ribbons: ChampionPaldea, BestFriends` | 英文 PKHeX 属性后缀，分别对应 `RibbonChampionPaldea`、`RibbonBestFriends`；也可写完整属性名。 |
| `Title: Mightiest` | 佩戴称号；可用英文 `RibbonIndex` 名字、对应数值 ID，或 `None`/`-1`。有效称号自动附带对应奖章/缎带。`Mightiest` 对应 `MarkMightiest`（108）；`Partner` 优先指 `MarkPartner`（104，同行之证），若要伙伴奖章请明确写 `RibbonPartner`。`Title: None` 只取消佩戴，不删除已经指定的奖章。 |
| `MetDate: 2024-01-01` | 相遇日期；别名 `Met Date`。推荐真实日期 `YYYY-MM-DD`；也兼容原生 ALM 八位 `yyyyMMdd`，年份 2000～2255。 |
| `EggMetDate: 2024-01-01` | 蛋相遇日期；别名 `EggDate`、`Egg Date`。同上。 |
| `MetLocation: 50` | 相遇地点的 **PKHeX 数值 ID**；别名 `Met Location`。不是中文地名，也不是跨游戏通用编号。 |
| `EggLocation: 60002` | 蛋相遇地点数值 ID；别名 `Egg Location`。 |
| `MetLevel: 50` | 相遇等级 1～100；别名 `Met Level`。与 PS 的当前 `Level:` 不同。 |
| `.Scale=128` | 原生 ALM batch 风格的白名单属性赋值；其他已登记属性见下节。 |

日期、地点、等级、奖章与尺寸必须同时符合生成的遭遇和 PKHeX 合法性规则；语法接受不等于该物种/遭遇可以拥有它们。上表地点数值仅说明语法，不是通用合法推荐值。

## 缺省与优先级

1. **显式输入优先保留**。生成后会核对指定配置；合法化若改变要求，不会悄悄交付替代品，而是报错。
2. OT、OTGender、TID、SID **逐字段独立处理**：手填哪些就保留哪些。启用 AutoOT 时，未填字段跟随实际接收方；只填 OT 不会冻结其他三个字段。不要为“跟随接收方”填占位值，也不要留 `OT:` 空行。
3. 未启用 AutoOT 时，未指定训练家字段使用生成器/宿主配置默认值，不保证是接收方资料。固定 OT 的活动遭遇等仍受合法性限制。
4. 未指定的尺寸、奖章、称号、日期、地点和相遇等级交由合法遭遇生成器选择。不要为了补齐模板而随意填写。
5. 同一扩展属性通过简便别名和 `.Field=value` 指定相同值可共存；不同值报冲突。OT 等训练家字段重复即报错。每只只能有一行 `EVs:`、`SPs:` 或 `SP:`，不能混用或重复。
6. `SPs:`/`SP:` 明确指定 SP；`EVs:` 只有在明确冠军格式作用域中才是 SP。**不会因为 EV 数值小就猜成 SP**。

AutoOT 的逐字段行为已有自动化覆盖，但这不等于已经实际收货验证所有组合。已有实机记录覆盖两只批次交换及主动退出；未据此宣称全部扩展均已实机验收。

## 冠军导出中的 EVs 歧义与转换边界

冠军工具可能沿用 PS 的 `EVs:` 字段名，却填入 0～32 的 SP。例如 `EVs: 32 HP / 32 Atk / 2 Spe` 单凭文本无法判断单位：它既可能是传统小额 EV，也可能是冠军导出。

- 普通 PS：保持 `Format: Showdown`（或不写格式头），`EVs:` 永远是传统 EV。
- 确知来自冠军：在该队伍前写 `Format: Champions`，或将投资行改成 `SPs:`。格式标识含 `champions` 的 PS 队伍标题也可明示单位。
- 后面接普通 PS 队伍时，写 `Format: Showdown` 或使用格式标识不含 `champions` 的新队伍标题，避免继承 SP 模式。

转换采用每项满足该增量的最少传统 EV：`SP = 0 → EV = 0`，`SP > 0 → EV = 8 × SP - 4`。这不是一律乘以 8，也不自动补满 510 EV。

**无损的限定范围**：传统 EV 游戏、50 级、有效 IV 为 31（包含确实以 31 计算的情况）时，保留性格修正前的属性增量。并不承诺整个游戏规则、训练过程或跨等级完全等价；低 IV、0 攻/0 速、非 50 级均不能用这个结论保证相同数值。性格的取整可能掩盖部分差异，固定 HP 的物种也不按通常 HP 投资公式增长。转换不会替你修改 `Level:` 或 `IVs:` 来满足前提。

**有损规则**：若最少 EV 的总和超过 510，反复减少当前最小的正 SP，优先保留大项；并列时按 `HP → Atk → Def → SpA → SpD → Spe` 顺序选择。不会给未请求的属性加点。返回的 `Notices` 会说明限定范围、是否无损、有损项及实际 EV；请在提交结果中检查提示，不要把成功入队等同于无损。

例：`32 HP / 32 Atk / 2 Spe` 转为 `252 HP / 252 Atk / 12 Spe`，总计 516，无法无损装入 510。按规则削减速度 1 SP，实际为 `252 HP / 252 Atk / 4 Spe`（508 EV），报告损失 1 点性格修正前增量。相比之下 `32 HP / 32 Atk / 1 Spe` 直接得到同样的 508 EV，但它对原请求是无损的。

## 可复制例子

下列三个完整示例均已通过当前版本实际生成回归，最强证章另用历史轰擂金刚猩配置验证为 EncounterMight9。测试覆盖生成结果，不等于已逐只实机收货。

### 1. 普通 PS：无需任何扩展

```text
Pikachu @ Light Ball
Ability: Static
Level: 50
EVs: 252 Atk / 4 SpD / 252 Spe
Jolly Nature
- Thunder Punch
- Quick Attack
- Iron Tail
- Protect
```

这里未填 OT 等字段；启用 AutoOT 时跟随接收方，不必复制额外行。

### 2. 冠军 SP 与部分训练家覆盖

```text
Target: SV
Format: Champions
Garchomp @ Leftovers
Ability: Rough Skin
Level: 50
EVs: 32 HP / 32 Atk / 2 Spe
Adamant Nature
OT: Alice
- Earthquake
- Dragon Claw
- Swords Dance
- Protect
```

该例有损，实际使用 `EVs: 252 HP / 252 Atk / 4 Spe` 并返回提示。只指定 OT；其余训练家字段仍可由 AutoOT 跟随接收方。也可删掉 `Format: Champions` 并把 `EVs:` 改成 `SPs:`，效果相同。

### 3. 最强证章与佩戴称号

```text
Charizard @ Charcoal
Ability: Solar Power
Level: 100
Shiny: No
EVs: 252 SpA / 4 SpD / 252 Spe
Modest Nature
IVs: 31 HP / 31 Atk / 31 Def / 31 SpA / 31 SpD / 31 Spe
Tera Type: Dragon
Title: Mightiest
- Flamethrower
- Dragon Pulse
- Focus Blast
- Sunny Day
```

`Title: Mightiest` 会自动要求 `RibbonMarkMightiest=true`；不必再写 `Marks: Mightiest`。**此例已通过当前版本实际生成测试，尚未以这份新配置实机收货。**最强证章必须匹配活动遭遇，不能给任意物种随意添加；若无法找到满足全部要求的合法遭遇，会失败而非删掉证章后派送。

## 原生 ALM batch 兼容与安全边界

兼容原生 PS 以及 **白名单内**的 ALM batch 赋值 `.Field=value`。目前登记范围：

- 当前实体类型实际提供的可读写 `Ribbon*` 属性，例如 `.RibbonMarkMightiest=true`、`.RibbonChampionPaldea=true`。
- `HeightScalar`、`WeightScalar`、`Scale`。
- `MetDate`、`EggMetDate`、`MetLocation`、`EggLocation`、`MetLevel`。
- `AffixedRibbon`（简便写法为 `Title:`）。

布尔值支持 `true/false`、`yes/no`、`1/0`。日期推荐 `YYYY-MM-DD`，也兼容 `.MetDate=20240101` 这样的原生八位日期；解析后统一交给原生 ALM 指令处理，不在生成后直接改日期绕开遭遇选择。`.AffixedRibbon=None` 可取消佩戴。训练家字段请使用 `OT:` 等简便行，不要假设 `.OriginalTrainerName=...` 可用。

这里的兼容 **不表示开放完整 ALM batch 语言**：任意 PKHeX 属性、筛选器、脚本或未知点号字段不在支持承诺内；未登记字段报错。遇到普通 PS 不认识的文字行也不要依赖其被忽略，应删除或使用上表明确支持的写法。

## 日期、地点与世代范围

- 日期采用实体存储范围 2000～2255，但特定活动时间、蛋相遇、先后关系等仍需合法；不能仅凭落在年份范围内就认定合法。
- 地点编号由目标格式及原始遭遇决定；同一数字在不同世代不一定代表同一地点。不要把网页地图编号或其他世代 PKHeX 编号直接套进来。
- PS 的传统 EV 单位可用于描述其他世代配置；但当前网页只生成并派送 PK9。`Format: Gen8` 等只是单位模式别名，**并未开启第八世代派送**。本网页目前还限定朱紫原生遭遇，不派送依赖旧世代转入的个体。
- 架构使用目标实体类型的 `PropertyInfo` 验证字段，可为其他世代增加适配；尚未提供的目标会明确拒绝。无传统 EV 的游戏不在冠军 SP 转换的等价范围内。

## 故障与扩展约定

一批会先解析、生成并核对全部成员，再一次性入队。任意一只出现字段不支持、重复/冲突、SP 超范围、日期错误、无法生成合法遭遇或指定配置被合法化改变，**整批失败，不会先把前几只部分入队**。这是提交阶段保证，不是交换过程的原子性保证；交换已开始后的取消或断线不能撤回已交付的宝可梦。

Showdown+ v1 是当前约定名称，不需要在文本中写版本头。后续扩展应保持普通 PS 的默认含义、显式单位与逐字段缺省行为；新属性需登记别名/白名单、目标实体支持、类型/范围验证、生成后核对及必要的合法性规则。新世代还需要生成与交换适配器，不能只因反射能找到属性就宣称支持跨代派送。


## 字段名称速查（PK9 / PKHeX 26.7.7）

以下是当前实体提供的可编辑名称，方便直接复制；**存在该字段不表示朱紫原生个体一定能合法获得该奖章**，旧世代限定奖章仍会受原生来源限制。升级PKHeX后新增属性由类型注册读取，但必须补充合法生成测试才作为已验证功能。

### Marks 可用名称

`Marks:` 后填下面去掉 `RibbonMark` 的名称；也可以写完整 `.RibbonMark名称=true`。

```properties
Lunchtime
SleepyTime
Dusk
Dawn
Cloudy
Rainy
Stormy
Snowy
Blizzard
Dry
Sandstorm
Misty
Destiny
Fishing
Curry
Uncommon
Rare
Rowdy
AbsentMinded
Jittery
Excited
Charismatic
Calmness
Intense
ZonedOut
Joyful
Angry
Smiley
Teary
Upbeat
Peeved
Intellectual
Ferocious
Crafty
Scowling
Kindly
Flustered
PumpedUp
ZeroEnergy
Prideful
Unsure
Humble
Thorny
Vigor
Slump
Jumbo
Mini
Itemfinder
Partner
Gourmand
Alpha
Mightiest
Titan
```

### Ribbons 可用名称

`Ribbons:` 后填下面去掉 `Ribbon` 的名称。

```properties
ChampionKalos
ChampionG3
ChampionSinnoh
BestFriends
Training
BattlerSkillful
BattlerExpert
Effort
Alert
Shock
Downcast
Careless
Relax
Snooze
Smile
Gorgeous
Royal
GorgeousRoyal
Artist
Footprint
Record
Legend
Country
National
Earth
World
Classic
Premier
Event
Birthday
Special
Souvenir
Wishing
ChampionBattle
ChampionRegional
ChampionNational
ChampionWorld
ChampionG6Hoenn
ContestStar
MasterCoolness
MasterBeauty
MasterCuteness
MasterCleverness
MasterToughness
ChampionAlola
BattleRoyale
BattleTreeGreat
BattleTreeMaster
ChampionGalar
TowerMaster
MasterRank
Hisui
TwinklingStar
ChampionPaldea
OnceInALifetime
Partner
```

计数型字段用原生数值语法：`.RibbonCountMemoryContest=0`、`.RibbonCountMemoryBattle=0`；非零值是否可用取决于来源，不能用 `Ribbons:` 的布尔列表代替计数。

### 与其他世代的适配边界

| 格式族 | 尺寸字段 | 佩戴称号 | 当前网页能否派送 |
| --- | --- | --- | --- |
| PK6 / PK7 | 没有这三个字段 | 没有 AffixedRibbon | 否 |
| PB7 | HeightScalar / WeightScalar | 没有 AffixedRibbon | 否 |
| PK8 / PB8 | HeightScalar / WeightScalar，无 Scale | 有 | 否 |
| PA8 / PK9 / PA9 | HeightScalar / WeightScalar / Scale | 有 | 仅 PK9 |

这些是当前依赖的实体能力，不是跨世代派送承诺。例如PK3反射中虽可见日期属性，实际不存储日期；新适配器必须验证真实存储和生成结果，不能只看同名属性。第六世代及更早的训练家ID是16位TID/SID；当前文档的六位显示ID规则仅用于已实现的PK9入口，未来旧世代适配要按目标版本解释，不能直接照搬。
