# enable_lc_page.ps1 —— 打开/恢复「水冷」页面的注册表门禁
#
# CCU.WinUI 判断是否显示水冷页面，靠的是（CommonHelper.RegistrySoftwareKeyRead，64 位视图）：
#     HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport
#         LiquidCoolingSupport        = 1    # 1 显示"水冷"入口
#         LiquidCoolingAutoModeSupport= 1    # 1 提供"自动"风扇档
# 本机若原厂没写这些值，就算水冷状态报上来了，界面上也找不到入口。
#
# 用法（需要管理员 PowerShell）：
#   .\enable_lc_page.ps1            # 打开
#   .\enable_lc_page.ps1 -Revert    # 删除这两个值（恢复原状）

param([switch]$Revert)

$path = 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport'
if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }

if ($Revert) {
    foreach ($n in 'LiquidCoolingSupport','LiquidCoolingAutoModeSupport') {
        Remove-ItemProperty -Path $path -Name $n -ErrorAction SilentlyContinue
    }
    "已删除 LiquidCoolingSupport / LiquidCoolingAutoModeSupport"
} else {
    New-ItemProperty -Path $path -Name 'LiquidCoolingSupport'         -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $path -Name 'LiquidCoolingAutoModeSupport' -Value 1 -PropertyType DWord -Force | Out-Null
    "已写入：LiquidCoolingSupport=1, LiquidCoolingAutoModeSupport=1"
}
Get-ItemProperty -Path $path | Format-List
