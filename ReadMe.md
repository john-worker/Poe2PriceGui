## Poe2PriceGui-介绍

```
POE2 物价补丁GUI版本: 为《Path of Exile 2》国服自动抓取通货物价，查询装备价值工具(实时物价指的是打入补丁那一刻的物价，刷新物价需要自己手动更新，也就是得重新打补丁。)。

注: 本工具仅用于学习和研究，不涉及任何商业用途，目前只支持国服(Wegame)。

工具目前还是一个Alpha版本，可能会有BUG和不完善的地方。

BUG反馈可以加QQ群反馈: 1001850913
```

价格补丁部分参考自开源项目 [weixiao030/poe2\_price](https://github.com/weixiao030/poe2_price)，在此致谢。

***

> ⚠️ **重要提示：** 本工具会修改游戏文件，和其他补丁一样**存在封号风险**。使用前请确认自己能接受风险，并在**关闭游戏后**再运行。
>
> <br />
>
> API Token 用于访问更稳定的 \_validate API 和查询完整历史价格数据。
>
> 当前可使用限免30天的API KEY（2026-07-15 23点到期）: 789486ce3baf2c4a7e18f4ba0b9aa4ab8edb9da64ca92bca10ca74c094cd8f8d
>
> 如果超过7月15日，请将设置中的API Token删除，走原始API, 后续会针对性的优化价格计算。

***

## 使用方法

```
例:D:\WeGameApps\rail_apps\流放之路：降临(2002052)

游戏道具价格显示:
设置-游戏目录(优先点击自动选择，如果不可以手动复制目录过来)-等待下方检测(显示wegame服，则成功)-价格查看-刷新价格-工具箱-生产补丁并安装

查价器:
设置-查价器-点击登录-点击开关-默认热键(ctrl+d)
```

## 更新日志

```
v1.0.3
1.修复查价器登录部分问题(暂时改为手动登录，取消自动登录)。
2.取消设置页面最下方提示。

v1.0.2
1.添加自动更新功能,使用Github Releases更新。

v1.0.1
1.添加除github外的BUG反馈途径。
2.优化readme，添加简易使用方式。

v1.0.0
1.添加GUI界面管理(主界面表格内价格支持手动修复)。
2.获取国服价格方式添加最新过滤异常价格的API(目前已内置最新的API接口，和免费30天试用版的Token, summary_validate接口)。
3.显示优化为1D以下显示为E的单位,1D以上显示为D的单位,低于1E的不显示价格。
4.添加日志系统，记录工具运行时的事件和错误信息。
5.添加查价器功能。

```

## 打包命令

```
old:
dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false

new:
1. 安装 vpk CLI 工具（仅需一次）
dotnet tool install -g vpk
2. 发布应用
dotnet publish Poe2PriceGui.csproj -c Release --self-contained -r win-x64 -o .\publish
3. 用 vpk 打包 Velopack 发布包
vpk pack --packId Poe2PriceGui --packVersion 1.0.2 --packDir .\publish --mainExe Poe2PriceGui.exe

```

## 界面预览

***

|            主界面            |           工具箱-补丁           |           游戏效果          |
| :-----------------------: | :------------------------: | :---------------------: |
| ![软件主界面](image/image.png) | ![工具箱-补丁](image/tools.png) | ![游戏效果](image/game.png) |

|            查价器-道具           |            查价器-价格           |             查价器-道具提示            |
| :-------------------------: | :-------------------------: | :-----------------------------: |
| ![查价器-道具](image/pirce1.png) | ![查价器-价格](image/price2.png) | ![查价器-道具提示](image/item-tip.png) |

***

## ⭐ Star 历史

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=john-worker/Poe2PriceGui&type=Date&theme=dark" />
  <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=john-worker/Poe2PriceGui&type=Date" />
  <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=john-worker/Poe2PriceGui&type=Date" />
</picture>
