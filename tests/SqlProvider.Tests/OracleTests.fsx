﻿#r @"..\..\bin\FSharp.Data.SqlProvider.dll"

open System
open FSharp.Data.Sql

[<Literal>]
let connStr = "Data Source=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=ORACLE)(PORT=1521)))(CONNECT_DATA=(SERVER=DEDICATED)(SERVICE_NAME=XE)));User Id=HR;Password=password;"

[<Literal>]
let resolutionFolder = @"D:\Appdev\SqlProvider\tests\SqlProvider.Tests"
FSharp.Data.Sql.Common.QueryEvents.SqlQueryEvent |> Event.add (printfn "Executing SQL: %s")

let processId = System.Diagnostics.Process.GetCurrentProcess().Id;

type HR = SqlDataProvider<Common.DatabaseProviderTypes.ORACLE, connStr, ResolutionPath = resolutionFolder, Owner = "HR">
let ctx = HR.GetDataContext()

type Employee = {
    EmployeeId : int32
    FirstName : string
    LastName : string
    HireDate : DateTime
}

//***************** Individuals ***********************//
let indv = ctx.``[HR].[EMPLOYEES]``.Individuals.``As FIRST_NAME``.``100, Steven``

indv.FIRST_NAME + " " + indv.LAST_NAME + " " + indv.EMAIL


//*************** QUERY ************************//
let employeesFirstName = 
    query {
        for emp in ctx.``[HR].[EMPLOYEES]`` do
        select (emp.FIRST_NAME, emp.LAST_NAME)
    } |> Seq.toList

let salesNamedDavid = 
    query {
            for emp in ctx.``[HR].[EMPLOYEES]`` do
            join d in ctx.``[HR].[DEPARTMENTS]`` on (emp.DEPARTMENT_ID = d.DEPARTMENT_ID)
            where (d.DEPARTMENT_NAME |=| [|"Sales";"IT"|] && emp.FIRST_NAME =% "David")
            select (d.DEPARTMENT_NAME, emp.FIRST_NAME, emp.LAST_NAME)
            
    } |> Seq.toList

let employeesJob = 
    query {
            for emp in ctx.``[HR].[EMPLOYEES]`` do
            for manager in emp.EMP_MANAGER_FK do
            join dept in ctx.``[HR].[DEPARTMENTS]`` on (emp.DEPARTMENT_ID = dept.DEPARTMENT_ID)
            where ((dept.DEPARTMENT_NAME |=| [|"Sales";"Executive"|]) && emp.FIRST_NAME =% "David")
            select (emp.FIRST_NAME, emp.LAST_NAME, manager.FIRST_NAME, manager.LAST_NAME )
    } |> Seq.toList

//Can map SQLEntities to a domain type
let topSales5ByCommission = 
    query {
        for emp in ctx.``[HR].[EMPLOYEES]`` do
        sortByDescending emp.COMMISSION_PCT
        select emp
        take 5
    } 
    |> Seq.map (fun e -> e.MapTo<Employee>())
    |> Seq.toList

#r @"..\..\packages\Newtonsoft.Json.6.0.3\lib\net45\Newtonsoft.Json.dll"

open Newtonsoft.Json

type OtherCountryInformation = {
    Id : string
    Population : int
}

type Country = {
    CountryId : string
    CountryName : string
    Other : OtherCountryInformation
}

//Can customise SQLEntity mapping
let countries = 
    query {
        for emp in ctx.``[HR].[COUNTRIES]`` do
        select emp
    } 
    |> Seq.map (fun e -> e.MapTo<Country>(fun (prop,value) -> 
                                               match prop with
                                               | "Other" -> 
                                                    if value <> null
                                                    then JsonConvert.DeserializeObject<OtherCountryInformation>(value :?> string) |> box
                                                    else Unchecked.defaultof<OtherCountryInformation> |> box
                                               | _ -> value
                                         )
               )
    |> Seq.toList

//************************ CRUD *************************//


let antartica =
    let result =
        query {
            for reg in ctx.``[HR].[REGIONS]`` do
            where (reg.REGION_ID = 5M)
            select reg
        } |> Seq.toList
    match result with
    | [ant] -> ant
    | _ -> 
        let newRegion = ctx.``[HR].[REGIONS]``.Create() 
        newRegion.REGION_NAME <- "Antartica"
        newRegion.REGION_ID <- 5M
        ctx.SubmitUpdates()
        newRegion

antartica.REGION_NAME <- "ant"
ctx.SubmitUpdates()

antartica.Delete()
ctx.SubmitUpdates()

//********************** Procedures **************************//

ctx.Procedures.ADD_JOB_HISTORY.Invoke(100M, DateTime(1993, 1, 13), DateTime(1998, 7, 24), "IT_PROG", 60M)

//Support for sprocs with no parameters
ctx.Procedures.SECURE_DML.Invoke()

//Support for sprocs that return ref cursors
let employees =
    [
      for e in ctx.Procedures.GET_EMPLOYEES.Invoke().CATCUR do
        yield e.MapTo<Employee>()
    ]

type Region = {
    RegionId : decimal
    RegionName : string
  //  RegionDescription : string
}

//Support for MARS procs
let locations_and_regions =
    let results = ctx.Procedures.GET_LOCATIONS_AND_REGIONS.Invoke()
    [
      for e in results.LOCATIONS do
        yield e.ColumnValues |> Seq.toList |> box
              
      for e in results.REGIONS do
        yield e.MapTo<Region>() |> box
    ]


//Support for sprocs that return ref cursors and has in parameters
let getemployees hireDate =
    let results = (ctx.Procedures.GET_EMPLOYEES_STARTING_AFTER.Invoke hireDate)
    [
      for e in results.RESULTS do
        yield e.MapTo<Employee>()
    ]

getemployees (new System.DateTime(1999,4,1))

//********************** Functions ***************************//

let fullName = ctx.Functions.EMP_FULLNAME.Invoke(100M).ReturnValue

//********************** Packaged Procs **********************//

ctx.Packages.TEST_PACKAGE.INSERT_JOB_HISTORY.Invoke(100M, DateTime(1993, 1, 13), DateTime(1998, 7, 24), "IT_PROG", 60M)

//********************** Packaged Funcs **********************//

let fullNamPkg = ctx.Packages.TEST_PACKAGE.FULLNAME.Invoke("Bull", "Colin").ReturnValue