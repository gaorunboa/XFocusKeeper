;运行脚本就能看见托盘处ahk图标变更了=ndtest.py(.1.#B.1.).ahk     

#SingleInstance Force  ; ★★★ 关键代码：强制只保留一个实例 ★★★解决AHK脚本二次启动导致托盘出现多个图标的问题。

; 自定义函数来查找第一个标记在文本中首次出现的位置  
FindFirstMarkerPosition(text, marker) {  
    position := InStr(text, marker)  
    return position  
}  

; 获取脚本的完整文件名（不包含路径）            
FileName = %A_ScriptName%      
;MsgBox, "--130--FileName="%FileName%

; 示例字符串  
startMarker := "(."  
; 调用自定义函数获取第一个标记的位置  
firstMarkerPosition := FindFirstMarkerPosition(FileName, startMarker)  
; 输出结果以验证  
;MsgBox, % "The first marker was found at position: " firstMarkerPosition  

; 截取最后一个点之前的所有内容
BaseName = % SubStr(FileName, 1, firstMarkerPosition -1)       
;MsgBox, % "BaseName: " BaseName  

; 原始字符串  
originalString := FileName 
old := BaseName  
newtext := ""  
replacedString := StrReplace(originalString, old, newtext)
replacedString := StrReplace(replacedString, ".ahk", newtext)
replacedString := StrReplace(replacedString, "(.", newtext) 
replacedString := StrReplace(replacedString, ".)", newtext)  
replacedString := Trim(replacedString)  ; 清除空白字符
; 打印替换后的字符串  
;MsgBox, %replacedString%  

; 这个文件名称=ndtest.py(.1.#Y.1.).ahk
; 如何提取以上文本中"(."左边的字符个数

EnvGet, path1, ZKAUTO_HOME
if (path1 == "")  path1 := "C:"
path1 := "C:"

file1 := ( "C:\opt\try\ver.win\tryhello\#Y.ico")
file1 := ( "C:\opt\try\ver.win\tryhello\" replacedString ".ico")
; MsgBox, (~2~)001 path1=%path1% ==file1=%file1%
; MsgBox, (~2~)001 path1=%path1% ==file1=%file1%
Menu, Tray, Icon, %file1%,1,1
;return
exe_file1 = "cmd.exe"          
dir    := A_ScriptDir          
bat_py_file1  = %A_ScriptDir%\%BaseName%         
;run1=%exe_file1%  /c %bat_py_file1%             
run1 := exe_file1 . " /c """ bat_py_file1 """"       ; 在路径两侧添加双引号  
run1 := """" bat_py_file1 """"       ; 在路径两侧添加双引号
run1 := "cmd.exe /k """ bat_py_file1 """" 
run1 := "cmd.exe /c """ bat_py_file1 """" 
;msgbox, BaseName1=%BaseName%,run1=%run1%

; 动态注册热键
hotkeyName := "#" replacedString  ; 组合成 #Y / #X 等形式
Hotkey, %hotkeyName%, RunScript   ; 动态绑定热键标签; 注册热键
return  ; ★★★ 重要：阻止继续执行下方热键标签 ★★★

; 热键定义区域
; #1::run %A_ScriptDir%\zvbs.1.vbs
; #2::run %A_ScriptDir%\zvbs.2.vbs

; 热键执行标签（被隔离不会自动执行）
RunScript:
    ; msgbox, (~61~)BaseName=%BaseName%,run1=%run1%      
    Run, % run1  ; 使用表达式语法更可靠=Run %run1%
    ; 结论：现代AHK脚本中始终推荐 Run, % run1。它更安全、简洁，且无需担心空格和引号问题。
return

; 热键执行标签
RunScript22:
    msgbox, (~71~)BaseName=%BaseName%,run1=%run1%      
    Run %run1%
return

