namespace FSharp.Data.Sql.Providers

open System
open System.Collections.Concurrent
open System.Data
open System.Data.SqlClient
open FSharp.Data.Sql
open FSharp.Data.Sql.Transactions
open FSharp.Data.Sql.Schema
open FSharp.Data.Sql.Common
open System.Xml

module MSSqlServerSsdt =
    let assemblyNames = [ "Microsoft.SqlServer.Management.SqlParser.dll" ]

    type SsdtTable = {
        Schema: string
        Name: string
        Columns: SsdtColumn list
        PrimaryKey: PrimaryKeyConstraint option
        ForeignKeys: ForeignKeyConstraint list
        IsView: bool
    }
    and SsdtColumn = {
        Name: string
        DataType: string
        AllowNulls: bool
        Identity: IdentitySpec option
        HasDefault: bool
    }
    and IdentitySpec = {
        Seed: int
        Increment: int
    }
    and ForeignKeyConstraint = {
        Name: string
        Columns: string list
        References: RefTable
    }
    and RefTable = {
        Schema: string
        Table: string
        Columns: string list
    }
    and PrimaryKeyConstraint = {
        Name: string
        Columns: string list
    }
    and SsdtViewColumn = {
        Name: string
        RefCol: SsdtColumn
    }

    let typeMappingsByName =
        let toInt = int >> Some
        // https://docs.microsoft.com/en-us/dotnet/framework/data/adonet/sql-server-data-type-mappings
        [ "UNIQUEIDENTIFIER", "System.Guid", DbType.Guid, toInt SqlDbType.UniqueIdentifier
          "BIT", "System.Boolean", DbType.Boolean, toInt SqlDbType.Bit
          "INT", "System.Int32", DbType.Int32, toInt SqlDbType.Int
          "BIGINT", "System.Int64", DbType.Int64, toInt SqlDbType.BigInt
          "SMALLINT", "System.Int16", DbType.Int16, toInt SqlDbType.SmallInt
          "TINYINT", "System.Byte", DbType.Byte, toInt SqlDbType.TinyInt
          "FLOAT", "System.Double", DbType.Double, toInt SqlDbType.Float
          "REAL", "System.Single", DbType.Single, toInt SqlDbType.Real
          "DECIMAL", "System.Decimal", DbType.Decimal, toInt SqlDbType.Decimal
          "NUMERIC", "System.Decimal", DbType.Decimal, toInt SqlDbType.Decimal
          "MONEY", "System.Decimal", DbType.Decimal, toInt SqlDbType.Money
          "SMALLMONEY", "System.Decimal", DbType.Decimal, toInt SqlDbType.SmallMoney
          "VARCHAR", "System.String", DbType.String, toInt SqlDbType.VarChar
          "NVARCHAR", "System.String", DbType.String, toInt SqlDbType.NVarChar
          "CHAR", "System.String", DbType.String, toInt SqlDbType.Char
          "NCHAR", "System.String", DbType.StringFixedLength, toInt SqlDbType.NChar
          "TEXT", "System.String", DbType.String, toInt SqlDbType.Text
          "NTEXT", "System.String", DbType.String, toInt SqlDbType.NText
          "DATETIMEOFFSET", "System.DateTimeOffset", DbType.DateTimeOffset, toInt SqlDbType.DateTimeOffset
          "DATE", "System.DateTime", DbType.Date, toInt SqlDbType.Date
          "DATETIME", "System.DateTime", DbType.DateTime, toInt SqlDbType.DateTime
          "DATETIME2", "System.DateTime", DbType.DateTime2, toInt SqlDbType.DateTime2
          "SMALLDATETIME", "System.DateTime", DbType.DateTime, toInt SqlDbType.SmallDateTime
          "TIME", "System.TimeSpan", DbType.Time, toInt SqlDbType.Time
          "VARBINARY", "System.Byte[]", DbType.Binary, toInt SqlDbType.VarBinary
          "BINARY", "System.Byte[]", DbType.Binary, toInt SqlDbType.Binary
          "IMAGE", "System.Byte[]", DbType.Binary, toInt SqlDbType.Image
          "ROWVERSION", "System.Byte[]", DbType.Binary, None
          "XML", "System.Xml.Linq.XElement", DbType.Xml, toInt SqlDbType.Xml
          "CURSOR", ((typeof<SqlEntity[]>).ToString()), DbType.Object, None
          "SQL_VARIANT", "System.Object", DbType.Object, toInt SqlDbType.Variant
          "GEOGRAPHY", "Microsoft.SqlServer.Types.SqlGeography", DbType.Object, Some 29
          "GEOMETRY", "Microsoft.SqlServer.Types.SqlGeometry", DbType.Object, Some 29
          "HIERARCHYID", "Microsoft.SqlServer.Types.SqlHierarchyId", DbType.Object, Some 29 ]
        |> List.map (fun (providerTypeName, clrType, dbType, providerType) ->
            providerTypeName,
            { TypeMapping.ProviderTypeName = Some providerTypeName
              TypeMapping.ClrType = clrType
              TypeMapping.DbType = dbType
              TypeMapping.ProviderType = providerType }
        )
        |> Map.ofList

    /// Dynamically loads the "Microsoft.SqlServer.Management.SqlParser" assembly and returns a table script parser functions.
    let buildDynamicTableScriptParser =
        lazy fun resolutionPath referencedAssemblies ->
            let assembly = Reflection.tryLoadAssemblyFrom resolutionPath referencedAssemblies assemblyNames
            let findType f =
                match assembly with
                | Choice1Of2(assembly) ->
                    let types =
                        try assembly.GetTypes()
                        with | :? System.Reflection.ReflectionTypeLoadException as e ->
                            let msgs = e.LoaderExceptions |> Seq.map(fun e -> e.GetBaseException().Message) |> Seq.distinct
                            let details = "Details: " + Environment.NewLine + String.Join(Environment.NewLine, msgs)
                            let platform = Reflection.getPlatform(System.Reflection.Assembly.GetExecutingAssembly())
                            failwith (e.Message + Environment.NewLine + details + (if platform <> "" then Environment.NewLine +  "Current execution platform: " + platform else ""))
                    types |> Array.find f
                | Choice2Of2(paths, errors) ->
                    let details =
                        match errors with
                        | [] -> ""
                        | x -> Environment.NewLine + "Details: " + Environment.NewLine + String.Join(Environment.NewLine, x)
                    failwithf "Unable to resolve assemblies. One of %s (e.g. from Nuget package %s) must exist in the paths: %s %s %s."
                        (String.Join(", ", assemblyNames |> List.toArray))
                        "Microsoft.SqlServer.Management.SqlParser"
                        Environment.NewLine
                        (String.Join(Environment.NewLine, paths |> Seq.filter(fun p -> not(String.IsNullOrEmpty p))))
                        details

            // Loads Microsoft.SqlServer.Management.SqlParser.Parser.Parser
            let parserType =  findType (fun t -> t.Name = "Parser")
            let parseResultType =  findType (fun t -> t.Name = "ParseResult")
            let sqlScriptType =  findType (fun t -> t.Name = "SqlScript")
            let parseMethod = parserType.GetMethod("Parse", [| typeof<string> |])
            let scriptProperty = parseResultType.GetProperty("Script")
            let xmlProperty = sqlScriptType.GetProperty("Xml")

            /// Parses a table script and returns an Xml string with a table schema.
            fun  (tableScript: string) ->
                let oResult = parseMethod.Invoke(null, [| box tableScript |])
                let oSqlScript = scriptProperty.GetValue(oResult)
                xmlProperty.GetValue(oSqlScript) :?> string


    let attMaybe (nm: string) (node: XmlNode) = 
        node.Attributes 
        |> Seq.cast<XmlAttribute> 
        |> Seq.tryFind (fun a -> a.Name = nm) 
        |> Option.map (fun a -> a.Value) 
    
    let att (nm: string) (node: XmlNode) = 
        attMaybe nm node |> Option.defaultValue ""

    /// Analyzes Microsoft SQL Parser XML results and returns an  SsdtTable model.
    let parseTableSchemaXml (tableSchemaXml: string) = 
        let doc = new XmlDocument()
        use rdr = new System.IO.StringReader(tableSchemaXml)
        doc.Load(rdr)
            
        let tblStatement = doc.SelectSingleNode("/SqlScript/SqlBatch/SqlCreateTableStatement")
        let tblSchemaName, tblObjectName = 
            let objId = tblStatement.SelectSingleNode("SqlObjectIdentifier")
            objId |> att "SchemaName", objId |> att "ObjectName"
    
        let columns = 
            tblStatement.SelectSingleNode("SqlTableDefinition").SelectNodes("SqlColumnDefinition")
            |> Seq.cast<XmlNode>
            |> Seq.map (fun cd -> 
                let colName = cd |> att "Name"
                let dataType = cd.SelectSingleNode("SqlDataTypeSpecification/SqlDataType") |> att "ObjectIdentifier"
                let constraints = cd.SelectNodes("SqlConstraint") |> Seq.cast<XmlNode> |> Seq.map (att "Type")
                let allowNulls = not (constraints |> Seq.exists (fun c -> c = "NotNull")) // default is allow nulls
                let identity = 
                    cd.SelectSingleNode("SqlColumnIdentity") 
                    |> Option.ofObj 
                    |> Option.map (fun n -> { Increment = n |> att "Increment" |> int; Seed = n |> att "Seed" |> int })
                let hasDefaultConstraint = cd.SelectSingleNode("SqlDefaultConstraint") <> null

                { SsdtColumn.Name= colName
                  SsdtColumn.DataType = dataType
                  SsdtColumn.AllowNulls = allowNulls
                  SsdtColumn.Identity = identity
                  SsdtColumn.HasDefault = hasDefaultConstraint }
            )
            |> Seq.toList
    
        let primaryKeyConstraint = 
            tblStatement.SelectSingleNode("SqlTableDefinition").SelectSingleNode("SqlPrimaryKeyConstraint")
            |> Option.ofObj
            |> Option.map (fun pkc -> 
                let name = pkc |> att "Name"
                let cols = 
                    pkc.SelectNodes("SqlIndexedColumn")
                    |> Seq.cast<XmlNode>
                    |> Seq.map (att "Name")
                    |> Seq.toList
                { PrimaryKeyConstraint.Name = name
                  PrimaryKeyConstraint.Columns = cols }
            )
    
        let foreignKeyConstraints = 
            tblStatement.SelectSingleNode("SqlTableDefinition").SelectNodes("SqlForeignKeyConstraint")
            |> Seq.cast<XmlNode> 
            |> Seq.map (fun fkc -> 
                let name = fkc |> att "Name"
    
                let children = fkc.ChildNodes |> Seq.cast<XmlNode> |> Seq.toList |> List.filter (fun c -> c.Name = "SqlIdentifier" || c.Name = "SqlObjectIdentifier")
                let idx = children |> List.findIndex (fun c -> c.Name = "SqlObjectIdentifier")
    
                let localColumnNodes = children |> Seq.take idx |> Seq.toList
                let refTableNode = children.[idx]
                let refColumnNodes = children |> Seq.skip (idx + 1) |> Seq.toList
    
                let fkCols = 
                    match localColumnNodes with
                    | _ :: fkCols -> fkCols |> List.map (att "Value")
                    | _ -> []
    
                let refCols = 
                    refColumnNodes
                    |> List.map (att "Value")
    
                
                let refTable =
                    { RefTable.Schema = refTableNode |> att "SchemaName"
                      RefTable.Table = refTableNode |> att "ObjectName"
                      RefTable.Columns = refCols }
    
                { ForeignKeyConstraint.Name = name
                  ForeignKeyConstraint.Columns = fkCols
                  ForeignKeyConstraint.References = refTable }
            )
            |> Seq.toList
        
        { SsdtTable.Schema = tblSchemaName 
          SsdtTable.Name = tblObjectName
          SsdtTable.Columns = columns
          SsdtTable.PrimaryKey = primaryKeyConstraint
          SsdtTable.ForeignKeys = foreignKeyConstraints
          SsdtTable.IsView = false }

    /// Analyzes Microsoft SQL Parser XML results and returns an SsdtView model.
    let parseViewSchemaXml (tablesByFullName: Map<string * string, SsdtTable>) (viewSchemaXml: string) = 
        let doc = new XmlDocument()
        use rdr = new System.IO.StringReader(viewSchemaXml)
        doc.Load(rdr)
        
        let viewDef = doc.SelectSingleNode("/SqlScript/SqlBatch/SqlCreateViewStatement/SqlViewDefinition")
        let viewSchemaName, viewObjectName = 
            let objId = viewDef.SelectSingleNode("SqlObjectIdentifier")
            objId |> att "SchemaName", objId |> att "ObjectName"
    
        // Represents all the types of view columns that we have implemented for parsing
        let (|SqlScalarRefExpression|SqlNullScalarExpression|Other|) (parentNode: XmlNode) =
            let ssre = parentNode.SelectSingleNode("SqlScalarRefExpression")
            if ssre <> null then SqlScalarRefExpression ssre
            else
                let snse = parentNode.SelectSingleNode("SqlNullScalarExpression")
                if snse <> null then SqlNullScalarExpression snse
                else Other
    
        let cols = 
            viewDef.SelectSingleNode("SqlQuerySpecification").SelectSingleNode("SqlSelectClause").SelectNodes("SqlSelectScalarExpression")
            |> Seq.cast<XmlNode>
            |> Seq.choose (fun ssce ->
                match ssce with
                | SqlScalarRefExpression exp -> 
                    let objId = exp.SelectSingleNode("SqlObjectIdentifier")
                    let schema = objId |> att "DatabaseName" // Not sure why these are the way they are...
                    let table = objId |> att "SchemaName"
                    let tableColumn = objId |> att "ObjectName"
                    let colName = ssce |> attMaybe "Alias" |> Option.defaultValue tableColumn
    
                    // Try to find related table/column, else ignore
                    tablesByFullName.TryFind(schema, table)
                    |> Option.bind (fun table -> table.Columns |> List.tryFind (fun c -> c.Name = tableColumn))
                    |> Option.map (fun refCol ->
                        { SsdtViewColumn.Name = colName
                          SsdtViewColumn.RefCol = refCol }
                    )
                | SqlNullScalarExpression exp -> 
                    let sqlId = ssce.SelectSingleNode("SqlIdentifier")
                    let colName = sqlId |> att "Value"
                    Some
                        { SsdtViewColumn.Name = colName
                          SsdtViewColumn.RefCol = 
                            { SsdtColumn.Name= colName
                              SsdtColumn.DataType = "SQL_VARIANT" // TODO: Could possibly manually parse XML comment to determine ref table.column
                              SsdtColumn.AllowNulls = false
                              SsdtColumn.Identity = None
                              SsdtColumn.HasDefault = false } }
                | Other -> 
                    None // Some view column type that is not yet handled...
            )
            |> Seq.toList
                
        { SsdtTable.Schema = viewSchemaName
          SsdtTable.Name = viewObjectName
          SsdtTable.Columns = cols |> List.map (fun c -> c.RefCol)
          SsdtTable.PrimaryKey = None
          SsdtTable.ForeignKeys = []
          SsdtTable.IsView = true }

    /// Searches the configured SsdtPath for one or more schema folders with "Table" subfolders.
    let findScripts (subfolderName: string) (ssdtPath: string) =
        let rootDir = System.IO.DirectoryInfo(ssdtPath)
        rootDir.EnumerateDirectories()                      // Search for potential schema directories (containing a "Table" directory)
        |> Seq.collect(fun maybeSchemaDir ->                // Search for "Tables" directories
            maybeSchemaDir.EnumerateDirectories()
            |> Seq.filter (fun d -> d.Name = subfolderName) 
        )
        |> Seq.collect (fun d -> d.EnumerateFiles("*.sql")) // Return table scripts

    let findTableScripts = findScripts "Tables"
    let findViewScripts = findScripts "Views"
            
    let readScriptFile (file: System.IO.FileInfo) =
        System.IO.File.ReadAllText(file.FullName)

    let parseTableCreateScripts dynamicScriptParser ssdtPath =
        ssdtPath
        |> findTableScripts
        |> Seq.map readScriptFile
        |> Seq.map dynamicScriptParser
        |> Seq.map parseTableSchemaXml
        |> Seq.toList

    let parseViewCreateScripts dynamicScriptParser ssdtPath (tables: SsdtTable list) =
        let tablesByName = tables |> List.map (fun t -> (t.Schema, t.Name), t) |> Map.ofList
        ssdtPath
        |> findViewScripts
        |> Seq.map readScriptFile
        |> Seq.map dynamicScriptParser
        |> Seq.map (parseViewSchemaXml tablesByName)
        |> Seq.toList

    let ssdtTableToTable (tbl: SsdtTable) =
        { Schema = tbl.Schema ; Name = tbl.Name ; Type =  if tbl.IsView then "VIEW" else "BASE TABLE" } // Type options: "VIEW" or "BASE TABLE"

    let ssdtColumnToColumn (tbl: SsdtTable) (col: SsdtColumn) =
        match typeMappingsByName.TryFind col.DataType with
        | Some typeMapping ->
            Some
                { Column.Name = col.Name
                  Column.TypeMapping = typeMapping
                  Column.IsNullable = col.AllowNulls
                  Column.IsPrimaryKey =
                    tbl.PrimaryKey
                    |> Option.map (fun pk -> pk.Columns |> List.exists (fun colName -> colName = col.Name))
                    |> Option.defaultValue false
                  Column.IsAutonumber = col.Identity <> None
                  Column.HasDefault = col.HasDefault
                  Column.IsComputed = false // Not supported (unable to parse computed column type)
                  Column.TypeInfo = None }  // Not supported (but could be)
        | None ->
            None

    let fkToRelationship (childTable: SsdtTable) (fk: ForeignKeyConstraint) =
        { Name = fk.Name
          PrimaryTable = Table.CreateFullName(fk.References.Schema, fk.References.Table)
          PrimaryKey = fk.References.Columns.Head
          ForeignTable = Table.CreateFullName(childTable.Schema, childTable.Name)
          ForeignKey = fk.Columns.Head }



type internal MSSqlServerProviderSsdt(resolutionPath: string, contextSchemaPath: string, referencedAssemblies: string [], tableNames: string, ssdtPath: string) =
    let schemaCache = SchemaCache.LoadOrEmpty(contextSchemaPath)
    let createInsertCommand = MSSqlServer.createInsertCommand schemaCache
    let createUpdateCommand = MSSqlServer.createUpdateCommand schemaCache
    let createDeleteCommand = MSSqlServer.createDeleteCommand schemaCache
    let myLock = new Object()
    // Remembers the version of each instance it connects to
    let mssqlVersionCache = ConcurrentDictionary<string, Lazy<Version>>()

    let dynamicScriptParser = MSSqlServerSsdt.buildDynamicTableScriptParser.Value resolutionPath referencedAssemblies
    let ssdtTables =
        lazy
            let tables = MSSqlServerSsdt.parseTableCreateScripts dynamicScriptParser ssdtPath
            let views = MSSqlServerSsdt.parseViewCreateScripts dynamicScriptParser ssdtPath tables
            tables @ views |> List.map (fun t -> t.Name, t) |> Map.ofList

    interface ISqlProvider with
        member __.GetLockObject() = myLock
        member __.GetTableDescription(con,tableName) = tableName
        member __.GetColumnDescription(con,tableName,columnName) = columnName
        member __.CreateConnection(connectionString) = new SqlConnection(connectionString) :> IDbConnection
        member __.CreateCommand(connection,commandText) = new SqlCommand(commandText, downcast connection) :> IDbCommand
        member __.CreateCommandParameter(param, value) =
            let p = SqlParameter(param.Name,value)
            p.DbType <- param.TypeMapping.DbType
            Option.iter (fun (t:int) -> p.SqlDbType <- Enum.ToObject(typeof<SqlDbType>, t) :?> SqlDbType) param.TypeMapping.ProviderType
            p.Direction <- param.Direction
            Option.iter (fun l -> p.Size <- l) param.Length
            match param.TypeMapping.ProviderTypeName with
            | Some "Microsoft.SqlServer.Types.SqlGeometry" -> p.UdtTypeName <- "Geometry"
            | Some "Microsoft.SqlServer.Types.SqlGeography" -> p.UdtTypeName <- "Geography"
            | Some "Microsoft.SqlServer.Types.SqlHierarchyId" -> p.UdtTypeName <- "HierarchyId"
            | _ -> ()
            p :> IDbDataParameter
        member __.ExecuteSprocCommand(con, inputParameters, returnCols, values:obj array) = MSSqlServer.executeSprocCommand con inputParameters returnCols values
        member __.ExecuteSprocCommandAsync(con, inputParameters, returnCols, values:obj array) = MSSqlServer.executeSprocCommandAsync con inputParameters returnCols values
        member __.CreateTypeMappings(con) = ()
        member __.GetSchemaCache() = schemaCache
        
        member __.GetTables(con,_) =
            let allowed = tableNames.Split([|','|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim())

            let filterByTableNames tbl =
                if allowed = [||] then true
                else allowed |> Array.exists (fun tblName -> String.Compare(tbl.Name, tblName, true) = 0)

            ssdtTables.Value
            |> Seq.map (fun kvp -> kvp.Value)
            |> Seq.map MSSqlServerSsdt.ssdtTableToTable
            |> Seq.map (fun tbl -> schemaCache.Tables.GetOrAdd(tbl.FullName, tbl))
            |> Seq.filter filterByTableNames
            |> Seq.toList

        member __.GetPrimaryKey(table) =
            match ssdtTables.Value.TryFind(table.Name) with
            |  Some { PrimaryKey = Some { Columns = [c] } } -> Some (c)
            | _ -> None

        member __.GetColumns(con,table) =        
            let columns =
                match ssdtTables.Value.TryFind(table.Name) with
                | Some ssdtTbl ->
                    ssdtTbl.Columns
                    |> List.map (MSSqlServerSsdt.ssdtColumnToColumn ssdtTbl)
                    |> List.choose id
                    |> List.map (fun col -> col.Name, col)
                | None -> []
                |> Map.ofList

            // Add PKs to cache
            columns
            |> Seq.map (fun kvp -> kvp.Value)
            |> Seq.iter (fun col ->
                if col.IsPrimaryKey then
                    schemaCache.PrimaryKeys.AddOrUpdate(table.FullName, [col.Name], fun key old -> 
                         match col.Name with 
                         | "" -> old 
                         | x -> match old with
                                | [] -> [x]
                                | os -> x::os |> Seq.distinct |> Seq.toList |> List.sort
                    ) |> ignore
            )

            // Add columns to cache
            schemaCache.Columns.AddOrUpdate(table.FullName, columns, fun x old -> match columns.Count with 0 -> old | x -> columns)

        member __.GetRelationships(con,table) =
            let ssdtTables = ssdtTables.Value
            schemaCache.Relationships.GetOrAdd(table.FullName, fun name ->
                // The table containing the foreign key is called the child table
                match ssdtTables.TryFind(table.Name) with
                | Some ssdtTbl ->
                    let parents =
                        ssdtTbl.ForeignKeys
                        |> List.map (MSSqlServerSsdt.fkToRelationship ssdtTbl)

                    let children =
                        ssdtTables
                        |> Seq.choose (fun kvp -> // Get all fks that reference this table
                            match kvp.Value.ForeignKeys |> List.filter (fun fk -> (fk.References.Schema, fk.References.Table) = (table.Schema, table.Name)) with
                            | [] -> None
                            | _ as fks -> Some (kvp.Value, fks)
                        )
                        |> Seq.collect (fun (childTable, fks) -> fks |> List.map (MSSqlServerSsdt.fkToRelationship childTable))
                        |> Seq.toList
                    children, parents
                | None -> [], []
            )

        member __.GetSprocs(con) = [] // Not implemented for SSDT
        member __.GetIndividualsQueryText(table,amount) = String.Empty // Not implemented for SSDT
        member __.GetIndividualQueryText(table,column) = String.Empty // Not implemented for SSDT

        member __.GenerateQueryText(sqlQuery,baseAlias,baseTable,projectionColumns,isDeleteScript, con) =
            // TODO: Copied from Providers.MsSqlServer -- maybe this code should be shared? (also exists in Providers.MsSqlServer.Dynamic)

            let parameters = ResizeArray<_>()
            // make this nicer later..
            let param = ref 0
            let nextParam() =
                incr param
                sprintf "@param%i" !param

            let createParam (value:obj) =
                let paramName = nextParam()
                let p = MSSqlServer.createOpenParameter(paramName,value)
                p :> IDbDataParameter

            let fieldParam (value:obj) =
                let paramName = nextParam()
                parameters.Add(MSSqlServer.createOpenParameter(paramName,value):> IDbDataParameter)
                paramName
                        
            let mssqlPaging =               
              match mssqlVersionCache.TryGetValue(con.ConnectionString) with
              // SQL 2008 and earlier do not support OFFSET
              | true, mssqlVersion when mssqlVersion.Value.Major < 11 -> MSSQLPagingCompatibility.RowNumber
              | _ -> MSSQLPagingCompatibility.Offset

            let rec fieldNotation (al:alias) (c:SqlColumnType) = 
                let buildf (c:Condition)= 
                    let sb = System.Text.StringBuilder()
                    let (~~) (t:string) = sb.Append t |> ignore
                    filterBuilder (~~) [c]
                    sb.ToString()
                let x = fieldNotation
                let colSprint =
                    match String.IsNullOrEmpty(al) with
                    | true -> sprintf "[%s]" 
                    | false -> sprintf "[%s].[%s]" al 
                match c with
                // Custom database spesific overrides for canonical functions:
                | SqlColumnType.CanonicalOperation(cf,col) ->
                    let column = fieldNotation al col
                    match cf with
                    // String functions
                    | Replace(SqlConstant(searchItm),SqlCol(al2, col2)) -> sprintf "REPLACE(%s,%s,%s)" column (fieldParam searchItm) (fieldNotation al2 col2)
                    | Replace(SqlCol(al2, col2),SqlConstant(toItm)) -> sprintf "REPLACE(%s,%s,%s)" column (fieldNotation al2 col2) (fieldParam toItm)
                    | Replace(SqlCol(al2, col2),SqlCol(al3, col3)) -> sprintf "REPLACE(%s,%s,%s)" column (fieldNotation al2 col2) (fieldNotation al3 col3)
                    | Replace(SqlConstant searchItm, SqlConstant toItm) -> sprintf "REPLACE(%s,%s,%s)" column (fieldParam searchItm) (fieldParam toItm)
                    | Substring(SqlConstant startPos) -> sprintf "SUBSTRING(%s, %s)" column (fieldParam startPos)
                    | Substring(SqlCol(al2, col2)) -> sprintf "SUBSTRING(%s, %s)" column (fieldNotation al2 col2)
                    | SubstringWithLength(SqlConstant startPos,SqlConstant strLen) -> sprintf "SUBSTRING(%s, %s, %s)" column (fieldParam startPos) (fieldParam strLen)
                    | SubstringWithLength(SqlConstant startPos,SqlCol(al2, col2)) -> sprintf "SUBSTRING(%s, %s, %s)" column (fieldParam startPos) (fieldNotation al2 col2)
                    | SubstringWithLength(SqlCol(al2, col2), SqlConstant strLen) -> sprintf "SUBSTRING(%s, %s, %s)" column (fieldNotation al2 col2) (fieldParam strLen)
                    | SubstringWithLength(SqlCol(al2, col2),SqlCol(al3, col3)) -> sprintf "SUBSTRING(%s, %s, %s)" column (fieldNotation al2 col2) (fieldNotation al3 col3)
                    | Trim -> sprintf "LTRIM(RTRIM(%s))" column
                    | Length -> sprintf "DATALENGTH(%s)" column
                    | IndexOf(SqlConstant search) -> sprintf "CHARINDEX(%s,%s)" (fieldParam search) column
                    | IndexOf(SqlCol(al2, col2)) -> sprintf "CHARINDEX(%s,%s)" (fieldNotation al2 col2) column
                    | IndexOfStart(SqlConstant search, SqlConstant startPos) -> sprintf "CHARINDEX(%s,%s,%s)" (fieldParam search) column (fieldParam startPos)
                    | IndexOfStart(SqlConstant search, SqlCol(al2, col2)) -> sprintf "CHARINDEX(%s,%s,%s)" (fieldParam search) column (fieldNotation al2 col2)
                    | IndexOfStart(SqlCol(al2, col2), SqlConstant startPos) -> sprintf "CHARINDEX(%s,%s,%s)" (fieldNotation al2 col2) column (fieldParam startPos)
                    | IndexOfStart(SqlCol(al2, col2), SqlCol(al3, col3)) -> sprintf "CHARINDEX(%s,%s,%s)" (fieldNotation al2 col2) column (fieldNotation al3 col3)
                    | CastVarchar -> sprintf "CAST(%s AS NVARCHAR(MAX))" column
                    // Date functions
                    | Date -> sprintf "CAST(%s AS DATE)" column
                    | Year -> sprintf "YEAR(%s)" column
                    | Month -> sprintf "MONTH(%s)" column
                    | Day -> sprintf "DAY(%s)" column
                    | Hour -> sprintf "DATEPART(HOUR, %s)" column
                    | Minute -> sprintf "DATEPART(MINUTE, %s)" column
                    | Second -> sprintf "DATEPART(SECOND, %s)" column
                    | AddYears(SqlConstant x) -> sprintf "DATEADD(YEAR, %s, %s)" (fieldParam x) column
                    | AddYears(SqlCol(al2, col2)) -> sprintf "DATEADD(YEAR, %s, %s)" (fieldNotation al2 col2) column
                    | AddMonths x -> sprintf "DATEADD(MONTH, %s, %s)" (fieldParam x) column
                    | AddDays(SqlConstant x) -> sprintf "DATEADD(DAY, %s, %s)" (fieldParam x) column // SQL ignores decimal part :-(
                    | AddDays(SqlCol(al2, col2)) -> sprintf "DATEADD(DAY, %s, %s)" (fieldNotation al2 col2) column
                    | AddHours x -> sprintf "DATEADD(HOUR, %f, %s)" x column
                    | AddMinutes(SqlConstant x) -> sprintf "DATEADD(MINUTE, %s, %s)" (fieldParam x) column
                    | AddMinutes(SqlCol(al2, col2)) -> sprintf "DATEADD(MINUTE, %s, %s)" (fieldNotation al2 col2) column
                    | AddSeconds x -> sprintf "DATEADD(SECOND, %f, %s)" x column
                    | DateDiffDays(SqlCol(al2, col2)) -> sprintf "DATEDIFF(DAY, %s, %s)" (fieldNotation al2 col2) column
                    | DateDiffSecs(SqlCol(al2, col2)) -> sprintf "DATEDIFF(SECOND, %s, %s)" (fieldNotation al2 col2) column
                    | DateDiffDays(SqlConstant x) -> sprintf "DATEDIFF(DAY, %s, %s)" (fieldParam x) column
                    | DateDiffSecs(SqlConstant x) -> sprintf "DATEDIFF(SECOND, %s, %s)" (fieldParam x) column
                    // Math functions
                    | Truncate -> sprintf "TRUNCATE(%s)" column
                    | BasicMathOfColumns(o, a, c) when o = "/" -> sprintf "(%s %s (1.0*%s))" column o (fieldNotation a c)
                    | BasicMathOfColumns(o, a, c) -> sprintf "(%s %s %s)" column (o.Replace("||","+")) (fieldNotation a c)
                    | BasicMath(o, par) when (par :? String || par :? Char) -> sprintf "(%s %s %s)" column (o.Replace("||","+")) (fieldParam par)
                    | BasicMathLeft(o, par) when (par :? String || par :? Char) -> sprintf "(%s %s %s)" (fieldParam par) (o.Replace("||","+")) column
                    | Greatest(SqlConstant x) -> sprintf "(SELECT MAX(V) FROM (VALUES (%s), (%s)) AS VALUE(V))" (fieldParam x) column
                    | Greatest(SqlCol(al2, col2)) -> sprintf "(SELECT MAX(V) FROM (VALUES (%s), (%s)) AS VALUE(V))" (fieldNotation al2 col2) column
                    | Least(SqlConstant x) -> sprintf "(SELECT MIN(V) FROM (VALUES (%s), (%s)) AS VALUE(V))" (fieldParam x) column
                    | Least(SqlCol(al2, col2)) -> sprintf "(SELECT MIN(V) FROM (VALUES (%s), (%s)) AS VALUE(V))" (fieldNotation al2 col2) column
                    | Pow(SqlCol(al2, col2)) -> sprintf "POWER(%s, %s)" column (fieldNotation al2 col2)
                    | Pow(SqlConstant x) -> sprintf "POWER(%s, %s)" column (fieldParam x)
                    | PowConst(SqlConstant x) -> sprintf "POWER(%s, %s)" (fieldParam x) column
                    //if-then-else
                    | CaseSql(f, SqlCol(al2, col2)) -> sprintf "CASE WHEN %s THEN %s ELSE %s END " (buildf f) column (fieldNotation al2 col2)
                    | CaseSql(f, SqlConstant itm) -> sprintf "CASE WHEN %s THEN %s ELSE %s END " (buildf f) column (fieldParam itm)
                    | CaseNotSql(f, SqlConstant itm) -> sprintf "CASE WHEN %s THEN %s ELSE %s END " (buildf f) (fieldParam itm) column
                    | CaseSqlPlain(f, itm, itm2) -> sprintf "CASE WHEN %s THEN %s ELSE %s END " (buildf f) (fieldParam itm) (fieldParam itm2)
                    | _ -> Utilities.genericFieldNotation (fieldNotation al) colSprint c
                | GroupColumn (StdDevOp key, KeyColumn _) -> sprintf "STDEV(%s)" (colSprint key)
                | GroupColumn (StdDevOp _,x) -> sprintf "STDEV(%s)" (fieldNotation al x)
                | GroupColumn (VarianceOp key, KeyColumn _) -> sprintf "VAR(%s)" (colSprint key)
                | GroupColumn (VarianceOp _,x) -> sprintf "VAR(%s)" (fieldNotation al x)
                | _ -> Utilities.genericFieldNotation (fieldNotation al) colSprint c

            and filterBuilder (~~) (f:Condition list) =
                // the filter expressions

                let rec filterBuilder' = function
                    | [] -> ()
                    | (cond::conds) ->
                        let build op preds (rest:Condition list option) =
                            ~~ "("
                            preds |> List.iteri( fun i (alias,col,operator,data) ->
                                    let column = fieldNotation alias col
                                    let extractData data =
                                            match data with
                                            | Some(x) when (box x :? System.Linq.IQueryable) -> [||]
                                            | Some(x) when (box x :? obj array) ->
                                                // in and not in operators pass an array
                                                let elements = box x :?> obj array
                                                Array.init (elements.Length) (fun i -> createParam (elements.GetValue(i)))
                                            | Some(x) -> [|createParam (box x)|]
                                            | None ->    [|createParam DBNull.Value|]

                                    let operatorIn operator (array : IDbDataParameter[]) =
                                        if Array.isEmpty array then
                                            match operator with
                                            | FSharp.Data.Sql.In -> "1=0" // nothing is in the empty set
                                            | FSharp.Data.Sql.NotIn -> "1=1" // anything is not in the empty set
                                            | _ -> failwith "Should not be called with any other operator"
                                        else
                                            let text = String.Join(",", array |> Array.map (fun p -> p.ParameterName))
                                            Array.iter parameters.Add array
                                            match operator with
                                            | FSharp.Data.Sql.In -> sprintf "%s IN (%s)" column text
                                            | FSharp.Data.Sql.NotIn -> sprintf "%s NOT IN (%s)" column text
                                            | _ -> failwith "Should not be called with any other operator"

                                    let prefix = if i>0 then (sprintf " %s " op) else ""
                                    let paras = extractData data

                                    let operatorInQuery operator (array : IDbDataParameter[]) =
                                        let innersql, innerpars = data.Value |> box :?> string * IDbDataParameter[]
                                        Array.iter parameters.Add innerpars
                                        match operator with
                                        | FSharp.Data.Sql.NestedExists -> sprintf "EXISTS (%s)" innersql
                                        | FSharp.Data.Sql.NestedNotExists -> sprintf "NOT EXISTS (%s)" innersql
                                        | FSharp.Data.Sql.NestedIn -> sprintf "%s IN (%s)" column innersql
                                        | FSharp.Data.Sql.NestedNotIn -> sprintf "%s NOT IN (%s)" column innersql
                                        | _ -> failwith "Should not be called with any other operator"

                                    ~~(sprintf "%s%s" prefix <|
                                        match operator with
                                        | FSharp.Data.Sql.IsNull -> sprintf "%s IS NULL" column
                                        | FSharp.Data.Sql.NotNull -> sprintf "%s IS NOT NULL" column
                                        | FSharp.Data.Sql.In 
                                        | FSharp.Data.Sql.NotIn -> operatorIn operator paras
                                        | FSharp.Data.Sql.NestedExists 
                                        | FSharp.Data.Sql.NestedNotExists 
                                        | FSharp.Data.Sql.NestedIn 
                                        | FSharp.Data.Sql.NestedNotIn -> operatorInQuery operator paras
                                        | _ ->
                                            let aliasformat = sprintf "%s %s %s" column
                                            match data with 
                                            | Some d when (box d :? alias * SqlColumnType) ->
                                                let alias2, col2 = box d :?> (alias * SqlColumnType)
                                                let alias2f = fieldNotation alias2 col2
                                                aliasformat (operator.ToString()) alias2f
                                            | _ ->
                                                parameters.Add paras.[0]
                                                aliasformat (operator.ToString()) paras.[0].ParameterName
                            ))
                            // there's probably a nicer way to do this
                            let rec aux = function
                                | x::[] when preds.Length > 0 ->
                                    ~~ (sprintf " %s " op)
                                    filterBuilder' [x]
                                | x::[] -> filterBuilder' [x]
                                | x::xs when preds.Length > 0 ->
                                    ~~ (sprintf " %s " op)
                                    filterBuilder' [x]
                                    ~~ (sprintf " %s " op)
                                    aux xs
                                | x::xs ->
                                    filterBuilder' [x]
                                    ~~ (sprintf " %s " op)
                                    aux xs
                                | [] -> ()

                            Option.iter aux rest
                            ~~ ")"

                        match cond with
                        | Or(preds,rest) -> build "OR" preds rest
                        | And(preds,rest) ->  build "AND" preds rest
                        | ConstantTrue -> ~~ " (1=1) "
                        | ConstantFalse -> ~~ " (1=0) "
                        | NotSupported x ->  failwithf "Not supported: %O" x
                        filterBuilder' conds
                filterBuilder' f

            let sb = System.Text.StringBuilder()

            let (~~) (t:string) = sb.Append t |> ignore

            match sqlQuery.Take, sqlQuery.Skip, sqlQuery.Ordering with
            | Some _, Some _, [] -> failwith "skip and take paging requires an orderBy clause."
            | _ -> ()

            let getTable x =
                match sqlQuery.Aliases.TryFind x with
                | Some(a) when x <> "" -> a
                | _ -> baseTable

            let singleEntity = sqlQuery.Aliases.Count = 0

            // build  the select statement, this is easy ...
            let selectcolumns =
                if projectionColumns |> Seq.isEmpty then "1" else
                String.Join(",",
                    [|for KeyValue(k,v) in projectionColumns do
                        let cols = (getTable k).FullName
                        let k = if k <> "" then k elif baseAlias <> "" then baseAlias else baseTable.Name
                        if v.Count = 0 then   // if no columns exist in the projection then get everything
                            for col in schemaCache.Columns.[cols] |> Seq.map (fun c -> c.Key) do
                                if singleEntity then yield sprintf "[%s].[%s] as '%s'" k col col
                                else yield sprintf "[%s].[%s] as '[%s].[%s]'" k col k col
                        else
                            for colp in v |> Seq.distinct do
                                match colp with
                                | EntityColumn col ->
                                    if singleEntity then yield sprintf "[%s].[%s] as '%s'" k col col
                                    else yield sprintf "[%s].[%s] as '[%s].[%s]'" k col k col
                                | OperationColumn(n,op) ->
                                    yield sprintf "%s as '%s'" (fieldNotation k op) n|])
                                
            // Create sumBy, minBy, maxBy, ... field columns
            let columns =
                let extracolumns =
                    match sqlQuery.Grouping with
                    | [] -> FSharp.Data.Sql.Common.Utilities.parseAggregates fieldNotation MSSqlServer.fieldNotationAlias sqlQuery.AggregateOp
                    | g  -> 
                        let keys = g |> List.map(fst) |> List.concat |> List.map(fun (a,c) ->
                            if sqlQuery.Aliases.Count < 2 then fieldNotation a c
                            else sprintf "%s as '%s'" (fieldNotation a c) (fieldNotation a c))
                        let aggs = g |> List.map(snd) |> List.concat
                        let res2 = FSharp.Data.Sql.Common.Utilities.parseAggregates fieldNotation MSSqlServer.fieldNotationAlias aggs |> List.toSeq
                        [String.Join(", ", keys) + (if List.isEmpty aggs || List.isEmpty keys then ""  else ", ") + String.Join(", ", res2)] 
                match extracolumns with
                | [] -> selectcolumns
                | h::t -> h

            // next up is the FROM statement which includes joins ..
            let fromBuilder() =
                sqlQuery.Links
                |> List.iter(fun (fromAlias, data, destAlias)  ->
                    let joinType = if data.OuterJoin then "LEFT OUTER JOIN " else "INNER JOIN "
                    let destTable = getTable destAlias
                    ~~  (sprintf "%s [%s].[%s] as [%s] on "
                            joinType destTable.Schema destTable.Name destAlias)
                    ~~  (String.Join(" AND ", (List.zip data.ForeignKey data.PrimaryKey) |> List.map(fun (foreignKey,primaryKey) ->
                        sprintf "%s = %s"
                            (fieldNotation (if data.RelDirection = RelationshipDirection.Parents then fromAlias else destAlias) foreignKey)
                            (fieldNotation (if data.RelDirection = RelationshipDirection.Parents then destAlias else fromAlias) primaryKey)
                        ))))

            let groupByBuilder groupkeys =
                groupkeys
                |> List.iteri(fun i (alias,column) ->
                    if i > 0 then ~~ ", "
                    ~~ (fieldNotation alias column))

            let orderByBuilder() =
                sqlQuery.Ordering
                |> List.iteri(fun i (alias,column,desc) ->
                    if i > 0 then ~~ ", "
                    ~~ (sprintf "%s %s" (fieldNotation alias column) (if not desc then "DESC " else "")))

            if isDeleteScript then
                ~~(sprintf "DELETE FROM [%s].[%s] " baseTable.Schema baseTable.Name)
            else 
                // SELECT
                if sqlQuery.Distinct && sqlQuery.Count then ~~(sprintf "SELECT COUNT(DISTINCT %s) " (columns.Substring(0, columns.IndexOf(" as "))))
                elif sqlQuery.Distinct then ~~(sprintf "SELECT DISTINCT %s%s " (if sqlQuery.Take.IsSome then sprintf "TOP %i " sqlQuery.Take.Value else "")   columns)
                elif sqlQuery.Count then ~~("SELECT COUNT(1) ")
                else
                    match sqlQuery.Skip, sqlQuery.Take with
                    | None, Some take -> ~~(sprintf "SELECT TOP %i %s " take columns)
                    | _ -> ~~(sprintf "SELECT %s " columns)
                //ROW_NUMBER
                match mssqlPaging,sqlQuery.Skip, sqlQuery.Take with
                | MSSQLPagingCompatibility.RowNumber, Some _, _ -> 
                    //INCLUDE order by clause in ROW_NUMBER () OVER() of CTE
                    if sqlQuery.Ordering.Length > 0 then
                        ~~", ROW_NUMBER() OVER(ORDER BY  "
                        orderByBuilder()
                        ~~" ) AS RN  "
                | _ -> ()
                // FROM
                let bal = if baseAlias = "" then baseTable.Name else baseAlias
                ~~(sprintf "FROM [%s].[%s] as [%s] " baseTable.Schema baseTable.Name bal)
                sqlQuery.CrossJoins |> Seq.iter(fun (a,t) -> ~~(sprintf ", [%s].[%s] as [%s] " t.Schema t.Name a))
            fromBuilder()
            // WHERE
            if sqlQuery.Filters.Length > 0 then
                // each filter is effectively the entire contents of each where clause in the LINQ query,
                // of which there can be many. Simply turn them all into one big AND expression as that is the
                // only logical way to deal with them.
                let f = [And([],Some sqlQuery.Filters)]
                ~~"WHERE "
                filterBuilder (~~) f

            // GROUP BY
            if sqlQuery.Grouping.Length > 0 then
                let groupkeys = sqlQuery.Grouping |> List.map(fst) |> List.concat
                if groupkeys.Length > 0 then
                    ~~" GROUP BY "
                    groupByBuilder groupkeys

            if sqlQuery.HavingFilters.Length > 0 then
                let keys = sqlQuery.Grouping |> List.map(fst) |> List.concat

                let f = [And([],Some (sqlQuery.HavingFilters |> CommonTasks.parseHaving fieldNotation keys))]
                ~~" HAVING "
                filterBuilder (~~) f

            // ORDER BY
            match mssqlPaging, sqlQuery.Skip, sqlQuery.Take with
            | MSSQLPagingCompatibility.Offset, _, _
            | MSSQLPagingCompatibility.RowNumber, None, _ ->
              if sqlQuery.Ordering.Length > 0 then
                  ~~"ORDER BY "
                  orderByBuilder()
            | _ -> 
              //when RowNumber compatibility with SKIP, ommit order by clause as it's already in CTE
              ()

            match sqlQuery.Union with
            | Some(UnionType.UnionAll, suquery, pars) ->
                parameters.AddRange pars
                ~~(sprintf " UNION ALL %s " suquery)
            | Some(UnionType.NormalUnion, suquery, pars) -> 
                parameters.AddRange pars
                ~~(sprintf " UNION %s " suquery)
            | Some(UnionType.Intersect, suquery, pars) -> 
                parameters.AddRange pars
                ~~(sprintf " INTERSECT %s " suquery)
            | Some(UnionType.Except, suquery, pars) -> 
                parameters.AddRange pars
                ~~(sprintf " EXCEPT %s " suquery)
            | None -> ()
        
            let sql = 
                match mssqlPaging with
                | MSSQLPagingCompatibility.RowNumber ->
                    let outerSb = System.Text.StringBuilder()
                    outerSb.Append "WITH CTE AS ( "  |> ignore
                    match sqlQuery.Skip, sqlQuery.Take with
                    | Some skip, Some take ->
                        outerSb.Append (sb.ToString()) |> ignore
                        outerSb.Append ")" |> ignore
                        outerSb.Append (sprintf "SELECT %s FROM CTE [%s] WHERE RN BETWEEN %i AND %i" columns (if baseAlias = "" then baseTable.Name else baseAlias) (skip+1) (skip+take))  |> ignore
                        outerSb.ToString()
                    | Some skip, None ->
                        outerSb.Append (sb.ToString()) |> ignore
                        outerSb.Append ")" |> ignore
                        outerSb.Append (sprintf "SELECT %s FROM CTE [%s] WHERE RN > %i " columns (if baseAlias = "" then baseTable.Name else baseAlias) skip)  |> ignore
                        outerSb.ToString()
                    | _ -> 
                      sb.ToString()
                | _ ->
                    match sqlQuery.Skip, sqlQuery.Take with
                    | Some skip, Some take ->
                        // Note: this only works in >=SQL2012
                        ~~ (sprintf "OFFSET %i ROWS FETCH NEXT %i ROWS ONLY" skip take)
                    | Some skip, None ->
                        // Note: this only works in >=SQL2012
                        ~~ (sprintf "OFFSET %i ROWS FETCH NEXT %i ROWS ONLY" skip System.UInt32.MaxValue)
                    | _ -> ()
                    sb.ToString()

            (sql,parameters)

        member this.ProcessUpdates(con, entities, transactionOptions, timeout) =
            // TODO: Copied from Providers.MsSqlServer -- maybe this code should be shared?

            let sb = Text.StringBuilder()

            CommonTasks.``ensure columns have been loaded`` (this :> ISqlProvider) con entities

            if entities.Count = 0 then 
                ()
            else
            use scope = TransactionUtils.ensureTransaction transactionOptions
            try
                // close the connection first otherwise it won't get enlisted into the transaction
                if con.State = ConnectionState.Open then con.Close()
                con.Open()
                // initially supporting update/create/delete of single entities, no hierarchies yet
                entities.Keys
                |> Seq.iter(fun e ->
                    match e._State with
                    | Created ->
                        let cmd = createInsertCommand con sb e
                        Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                        if timeout.IsSome then
                            cmd.CommandTimeout <- timeout.Value
                        let id = cmd.ExecuteScalar()
                        CommonTasks.checkKey schemaCache.PrimaryKeys id e
                        e._State <- Unchanged
                    | Modified fields ->
                        let cmd = createUpdateCommand con sb e fields
                        Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                        if timeout.IsSome then
                            cmd.CommandTimeout <- timeout.Value
                        cmd.ExecuteNonQuery() |> ignore
                        e._State <- Unchanged
                    | Delete ->
                        let cmd = createDeleteCommand con sb e
                        Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                        if timeout.IsSome then
                            cmd.CommandTimeout <- timeout.Value
                        cmd.ExecuteNonQuery() |> ignore
                        // remove the pk to prevent this attempting to be used again
                        e.SetPkColumnOptionSilent(schemaCache.PrimaryKeys.[e.Table.FullName], None)
                        e._State <- Deleted
                    | Deleted | Unchanged -> failwith "Unchanged entity encountered in update list - this should not be possible!")
                                   // but is possible if you try to use same context on multiple threads. Don't do that.
                if scope<>null then scope.Complete()

            finally
                con.Close()

        member this.ProcessUpdatesAsync(con, entities, transactionOptions, timeout) =
            // TODO: Copied from Providers.MsSqlServer -- maybe this code should be shared?

            let sb = Text.StringBuilder()

            CommonTasks.``ensure columns have been loaded`` (this :> ISqlProvider) con entities

            if entities.Count = 0 then 
                async { () }
            else

            async {
                use scope = TransactionUtils.ensureTransaction transactionOptions
                try
                    // close the connection first otherwise it won't get enlisted into the transaction
                    if con.State = ConnectionState.Open then con.Close()
                    do! con.OpenAsync() |> Async.AwaitIAsyncResult |> Async.Ignore
                    // initially supporting update/create/delete of single entities, no hierarchies yet
                    let handleEntity (e: SqlEntity) =
                        match e._State with
                        | Created ->
                            async {
                                let cmd = createInsertCommand con sb e
                                Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                                if timeout.IsSome then
                                    cmd.CommandTimeout <- timeout.Value
                                let! id = cmd.ExecuteScalarAsync() |> Async.AwaitTask
                                CommonTasks.checkKey schemaCache.PrimaryKeys id e
                                e._State <- Unchanged
                            }
                        | Modified fields ->
                            async {
                                let cmd = createUpdateCommand con sb e fields
                                Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                                if timeout.IsSome then
                                    cmd.CommandTimeout <- timeout.Value
                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                e._State <- Unchanged
                            }
                        | Delete ->
                            async {
                                let cmd = createDeleteCommand con sb e
                                Common.QueryEvents.PublishSqlQueryCol con.ConnectionString cmd.CommandText cmd.Parameters
                                if timeout.IsSome then
                                    cmd.CommandTimeout <- timeout.Value
                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                // remove the pk to prevent this attempting to be used again
                                e.SetPkColumnOptionSilent(schemaCache.PrimaryKeys.[e.Table.FullName], None)
                                e._State <- Deleted
                            }
                        | Deleted | Unchanged -> failwith "Unchanged entity encountered in update list - this should not be possible!"

                    do! Utilities.executeOneByOne handleEntity (entities.Keys|>Seq.toList)
                    if scope<>null then scope.Complete()

                finally
                    con.Close()
            }



    
