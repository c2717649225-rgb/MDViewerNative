# 宽内容回归语料

覆盖「宽表格横向滚动」与「项内嵌代码块横向滚动」两类结构。语料原有六个文件都不含这两类
结构，两次改动的金标准都没能拦住，故单列此文件补网。改动布局时这里必须有反应。

## 一、宽表格：多列溢出，首列够窄可冻结

| 项目名称 | 负责人 | 开始日期 | 结束日期 | 当前状态 | 优先级 | 预算金额 | 备注说明 |
|---|---|---|---|---|---|---|---|
| 子系统重构第一期 | 张三丰 | 2026-01-01 | 2026-06-30 | 进行中 | 高 | 1250000 元 | 依赖上游接口冻结 |
| 子系统重构第二期 | 李四光 | 2026-07-01 | 2026-12-31 | 未开始 | 中 | 980000 元 | 等待一期验收结论 |
| 数据迁移与校验 | 王五行 | 2026-03-15 | 2026-09-30 | 进行中 | 高 | 2100000 元 | 需双跑比对一个月 |

## 二、宽表格：首列过宽，不触发冻结

| 这一列的表头本身就非常长以至于超过视口的一半宽度用来验证不冻结分支 | B 列 | C 列 | D 列 | E 列 | F 列 |
|---|---|---|---|---|---|
| 同样很长的单元格内容，确保这一列的自然宽度确实超过视口一半 | 内容一 | 内容二 | 内容三 | 内容四 | 内容五 |

## 三、窄表格：不溢出，不应受横向滚动改动影响

| 左 | 右 |
|---|---|
| 1 | 2 |

## 四、对齐表格：溢出且带对齐标记

| 左对齐列内容较长一些 | 居中对齐列内容也不短 | 右对齐列同样占位 | 第四列 | 第五列 |
|:---|:---:|---:|---|---|
| aaaaaaaaaaaa | bbbbbbbbbbbb | cccccccccccc | dddd | eeee |

## 五、列表项内嵌代码块：长行溢出

- 第一项，项内代码块不折行：

    ```
    def extremely_long_function_signature(alpha, bravo, charlie, delta, echo, foxtrot, golf, hotel, india):
        return alpha + bravo + charlie + delta + echo + foxtrot + golf + hotel + india
    ```

- 第二项，项内短代码不溢出：

    ```
    ok
    ```

1. 有序列表内也放一个长行代码块：

    ```
    SELECT column_one, column_two, column_three, column_four, column_five FROM a_very_long_table_name WHERE id = 42;
    ```

## 六、引用内代码块（实为顶层块，走顶层路径）

> 引用里的围栏代码块：
>
> ```
> git log --oneline --graph --decorate --all --date=short --pretty=format:"%h %ad %s" | head -40
> ```

## 七、顶层宽代码块与表格混排

```python
def top_level_wide(argument_one, argument_two, argument_three, argument_four, argument_five=None):
    return {"one": argument_one, "two": argument_two, "three": argument_three}
```

| 紧跟代码块的表格 | 列二 | 列三 | 列四 | 列五 | 列六 | 列七 |
|---|---|---|---|---|---|---|
| 值一 | 值二 | 值三 | 值四 | 值五 | 值六 | 值七 |

## 八、表格单元格内含链接与行内码

| 名称 | 链接 | 行内码 | 说明文字稍长一些 | 第五列 | 第六列 |
|---|---|---|---|---|---|
| 示例 | [站点](https://example.com/some/deep/path) | `inline_code_here()` | 用于验证表格链接命中在滚动后仍正确 | 占位 | 占位 |
