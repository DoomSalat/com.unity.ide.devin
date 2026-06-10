# VS Tools: no installations discovered

## Проблема

На Unity **< 6000.5** пакет `com.unity.ide.visualstudio` 2.0.27 не находит установки Visual Studio.

В консоли:
```
[Devin] VS Tools: no installations discovered. Is Visual Studio installed?
```

В **Edit → Preferences → External Tools** (секция Devin) — предупреждение что IDE не найдена, хотя VS установлена.

## Причина

В `FileUtility.GetAbsolutePath` есть ветка:

```csharp
#if UNITY_6000_5_OR_NEWER
    return UnityEditor.FileUtil.PathToAbsolutePath(path); // корректно резолвит Packages/
#else
    return Path.GetFullPath(path); // просто CWD + путь — Packages/ не существует на диске
#endif
```

На Unity < 6000.5 путь к `vswhere.exe` резолвится в несуществующий:
`{project}/Packages/com.unity.ide.visualstudio/Editor/VSWhere/vswhere.exe`

Вместо реального:
`{project}/Library/PackageCache/com.unity.ide.visualstudio@.../Editor/VSWhere/vswhere.exe`

`vswhere.exe` не запускается → discovery возвращает пустой словарь → `Installations` пустой.

## Решение

Откатить пакет до версии без этого разделения. В `Packages/manifest.json`:

```json
"com.unity.ide.visualstudio": "2.0.22"
```

Перезапустить Unity — пакет переустановится, VS будет найдена.
