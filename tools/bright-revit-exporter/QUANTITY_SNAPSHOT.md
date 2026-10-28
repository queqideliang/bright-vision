# 构件计量快照 v1

本契约用于原始构件量表，数据来自明确的 Revit 实例内置参数，不定义清单／定额扣减、不以显示网格估算面积或体积。数量范围是导出视图实际写出的构件；链接实例分别保留身份。

每条 components 记录可增加 `QuantitySnapshot`，示例：

```json
{
  "schemaVersion": 1,
  "source": "revit-parameters",
  "sourceElement": {
    "uniqueId": "element-unique-id",
    "elementId": "123",
    "documentScope": "document-id",
    "linkInstanceScope": "root"
  },
  "metrics": {
    "length": { "status": "available", "value": 3.048, "unit": "m", "parameter": "CURVE_ELEM_LENGTH" },
    "area": { "status": "missing", "value": null, "unit": "m2", "parameter": "HOST_AREA_COMPUTED" },
    "volume": { "status": "available", "value": 0, "unit": "m3", "parameter": "HOST_VOLUME_COMPUTED" }
  }
}
```

固定指标为 length、area、volume。三个参数分别对应 `CURVE_ELEM_LENGTH`、`HOST_AREA_COMPUTED`、`HOST_VOLUME_COMPUTED`；不按中文显示名猜测参数，也不回退到类型参数。上述 Revit 参数的业务口径保留原样，不把它们命名为结算净量或毛量。

实例参数为 Double、有值且为非负有限数时 `available`，值按 Revit 内部英尺换算至 m／m²／m³：分别乘以 `0.3048`、`0.3048²`、`0.3048³`。零是合法结果。参数不存在或无值为 `missing`，错误存储类型／负值／非有限数为 `invalid`，读取 API 失败为 `unavailable`；不可用值写 null，不能写零替代。

视图可见性或剖切影响哪些构件进入导出范围，快照仍读取相应完整 Element 的原生参数；不会按被裁剪的显示网格重新量取面积或体积。

读取端验证 schemaVersion、来源标识、每个指标的单位、参数标识和数值状态。未知版本、来源、错误单位或不合约的数据不能参与该指标求和。旧 ZIP 缺少快照时仍可查看及统计构件数，量值明确显示缺失，提示使用新版插件重新导出。

量表默认跟随查看器搜索、类别和楼层筛选，并可切换为当前选择。同一 Revit 构件可能产生多条场景记录；有效快照先按来源文档、链接实例、UniqueId 和 ElementId 四元组全局归并，每个来源为一个“计量对象”，保留全部场景构件 ID 用于定位。构件模式一行一个计量对象，类别和楼层模式再对这些对象分组；不同链接实例分别计量。同一来源的某指标状态或值不一致时，该指标记为 invalid，不能静默选取第一条。没有有效来源身份的旧／坏快照按场景构件 ID 各自计数。

界面分别显示“计量对象数”和“模型记录数”，二者差值为重复来源记录数。同一来源的楼层归属取所有场景记录的并集，在多个楼层时归入单独的“多楼层（未拆分）”组，避免同一总量重复计入不同楼层；类别冲突使用“类别不一致”组。筛选或选择只决定涉及哪些完整来源对象；来源归并和冲突判定必须以完整模型为依据，不能通过只选其中一条记录消除已知冲突或楼层归属。相关场景记录数及定位 ID 包括该来源在完整模型中的全部记录。隐藏／剖切不会按可见网格裁剪或重算量值；“当前选择”模式跟随选中对象，隐藏导致取消选择时该范围也随之清空。

汇总按 length／area／volume 分列，禁止把不同维度混成一个合计；每列给出有效数与缺失／无效数，缺失全部时总量为空。总数和求和以原始精度计算，只在显示时舍入。CSV 采用 UTF-8 BOM，转义引号／换行，并保护构件名称等不可信文本的表格公式注入；导出包含范围、单位、数据来源和有效数量。
