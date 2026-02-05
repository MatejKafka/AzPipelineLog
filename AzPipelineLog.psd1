@{
    RootModule = './bin/Release/net10.0/AzPipelineLog.dll'
    ModuleVersion = '0.0.1'
    GUID = '060c0751-1a34-4fe7-9cf7-9db078d4fe25'
    Author = 'Matej Kafka'

    FunctionsToExport = @()
    CmdletsToExport = @('Get-AzPipelineLog')
    VariablesToExport = @()
    AliasesToExport = @()

    FormatsToProcess = @(
        'AzPipelineLog.Format.ps1xml'
    )
}
