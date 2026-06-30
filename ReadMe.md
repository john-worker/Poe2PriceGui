## Poe2PriceGui-介绍
```
POE2 物价补丁GUI版本: 为《Path of Exile 2》国服自动抓取通货物价，查询装备价值工具

注: 本工具仅用于学习和研究，不涉及任何商业用途，目前只支持国服(Wegame)。

工具目前还是一个Alpha版本，可能会有bug和不完善的地方。
有什么问题可以加QQ群反馈: 1001850913
```
价格补丁部分参考自开源项目 [weixiao030/poe2_price](https://github.com/weixiao030/poe2_price)，在此致谢。

---

> ⚠️ **重要提示：** 本工具会修改游戏文件，和其他补丁一样**存在封号风险**。使用前请确认自己能接受风险，并在**关闭游戏后**再运行。

---

## 更新日志
```
v1.0.0
1.添加GUI界面管理(主界面表格内价格支持手动修复)。
2.获取国服价格方式添加最新过滤异常价格的API(目前已内置最新的API接口，和免费30天试用版的Token)。
3.显示优化为1D以下显示为E的单位,1D以上显示为D的单位。
4.添加日志系统，记录工具运行时的事件和错误信息。
5.添加查价器功能。

```

## 打包命令
```
dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false
```


## 界面预览
---

| 主界面 | 工具箱-补丁 | 游戏效果 |
| :---: | :---: | :---: |
| ![软件主界面](image/image.png) | ![工具箱-补丁](image/tools.png) | ![游戏效果](image/game.png) |

| 查价器-道具 | 查价器-价格 | 查价器-道具提示 |
| :---: | :---: | :---: |
| ![查价器-道具](image/pirce1.png) | ![查价器-价格](image/price2.png) | ![查价器-道具提示](image/item-tip.png) |

---