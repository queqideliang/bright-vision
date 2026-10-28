function Get-RevitHostRuntime([string]$Root, [string]$Year) {
    if ([int]$Year -le 2024) { return 'netframework' }
    foreach ($name in @('RevitAPI.runtimeconfig.json', 'RevitNET.runtimeconfig.json', 'Revit.runtimeconfig.json')) {
        $file = [IO.Path]::Combine($Root, $name)
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        try {
            if ((Get-Item -LiteralPath $file).Length -gt 1048576) { continue }
            $config = [IO.File]::ReadAllText($file) | ConvertFrom-Json
            $options = $config.runtimeOptions
            if ($options.PSObject.Properties['tfm'] -and [string]$options.tfm -match '^net(8|10)\.0(?:-windows.*)?$') { return ('net' + $Matches[1]) }
            if ($options.PSObject.Properties['frameworks']) {
                foreach ($framework in $options.frameworks) {
                    if ($framework.name -eq 'Microsoft.NETCore.App' -and [string]$framework.version -match '^(8|10)\.') { return ('net' + $Matches[1]) }
                }
            }
        }
        catch { continue }
    }
    return 'unknown'
}

function Get-DetectedRevitHosts {
    $found = @{}
    $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
    foreach ($root in $roots) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        foreach ($entry in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
            $item = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue
            if (!$item -or !$item.PSObject.Properties['DisplayName'] -or !$item.PSObject.Properties['InstallLocation']) { continue }
            if ([string]$item.DisplayName -notmatch '^Autodesk Revit (201[5-9]|202[0-6])(?:\s|$)') { continue }
            $year = $Matches[1]
            $location = ([string]$item.InstallLocation).Trim().Trim('"')
            if ([string]::IsNullOrWhiteSpace($location)) { continue }
            foreach ($candidate in @($location, [IO.Path]::Combine($location, "Revit $year"))) {
                $exe = [IO.Path]::Combine($candidate, 'Revit.exe')
                if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
                $directory = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
                if ($found.ContainsKey($year)) {
                    if ($found[$year].Root -ne $directory) { $found[$year].Runtime = 'ambiguous'; $found[$year].Notes = 'More than one installation directory is registered for this year.' }
                    continue
                }
                $found[$year] = [pscustomobject]@{
                    Year = $year; Root = $directory; Runtime = (Get-RevitHostRuntime $directory $year)
                    FileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion; Notes = ''
                }
            }
        }
    }
    return @($found.Values | Sort-Object Year)
}
