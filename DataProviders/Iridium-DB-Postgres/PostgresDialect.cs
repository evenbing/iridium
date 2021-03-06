﻿#region License
//=============================================================================
// Iridium - Porable .NET ORM 
//
// Copyright (c) 2015-2017 Philippe Leybaert
//
// Permission is hereby granted, free of charge, to any person obtaining a copy 
// of this software and associated documentation files (the "Software"), to deal 
// in the Software without restriction, including without limitation the rights 
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//=============================================================================
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Iridium.Core;

namespace Iridium.DB.Postgres
{
    public class PostgresDialect : SqlDialect
    {
        public override string QuoteField(string fieldName)
        {
            return $"\"{fieldName.Replace(".", "\".\"")}\"";
        }

        public override string QuoteTable(string tableName)
        {
            return $"\"{tableName.Replace("\"", "\".\"")}\"";
        }

        public override string CreateParameterExpression(string parameterName)
        {
            return "@" + parameterName;
        }

        public override string SelectSql(SqlTableNameWithAlias tableName, IEnumerable<SqlExpressionWithAlias> columns, string sqlWhere, IEnumerable<SqlJoinDefinition> joins = null, string sqlSortExpression = null, int? start = null, int? numRecords = null, string afterSelect = null)
        {
            if (start == null && numRecords == null)
                return base.SelectSql(tableName, columns, sqlWhere, joins, sqlSortExpression);

            if (start == null)
                return base.SelectSql(tableName, columns, sqlWhere, joins, sqlSortExpression, numRecords: numRecords);

            string sql = base.SelectSql(tableName, columns, sqlWhere, joins, sqlSortExpression, numRecords: numRecords);

            if (start.Value > 1)
                sql += " OFFSET " + (start-1);

            return sql;
        }

        public override string DeleteSql(SqlTableNameWithAlias tableName, string sqlWhere, IEnumerable<SqlJoinDefinition> joins)
        {
            if (joins != null && joins.Any())
            {
                string sql = "delete from " + QuoteTable(tableName.TableName) + (tableName.Alias != null ? (" " + tableName.Alias + " ") : "");

                sql += "using " +  string.Join(",", joins.Select(join => QuoteTable(join.Right.Schema.MappedName) + " " + join.Right.Alias));

                if (sqlWhere != null)
                    sql += $" where ({sqlWhere})";

                sql += " and ";

                sql += string.Join(" and ", joins.Select(join => QuoteField(join.Left.Alias + "." + @join.Left.Field.MappedName) + "=" + QuoteField(join.Right.Alias + "." + @join.Right.Field.MappedName)));

                return sql;
            }

            return "delete from " + QuoteTable(tableName.TableName) + (tableName.Alias != null ? (" " + tableName.Alias + " ") : "") + (sqlWhere != null ? (" where " + sqlWhere) : "");
        }

        public override string InsertOrUpdateSql(string tableName, StringPair[] columns, string[] keyColumns, string sqlWhere)
        {
            var parts = new List<string>
            {
                "insert into",
                QuoteTable(tableName),
                '(' + string.Join(",", columns.Select(c => QuoteField(c.Key))) + ')',
                "values",
                "(" + string.Join(",", columns.Select(c => c.Value)) + ")",
                "on conflict",
                "(" + string.Join(",", keyColumns.Select(c => QuoteField(c))) + ')' ,
                "do update set",
                string.Join(",", columns.Select(c => $"{QuoteField(c.Key)}={c.Value}")),
            };

            return string.Join(" ", parts);
        }

        public override string GetLastAutoincrementIdSql(string columnName, string alias, string tableName)
        {
            return "select lastval() as " + alias;
        }

        public override void CreateOrUpdateTable(TableSchema schema, bool recreateTable, bool recreateIndexes, SqlDataProvider dataProvider)
        {
            const string longTextType = "TEXT";

            var columnMappings = new[]
            {
                new {Flags = TypeFlags.Array | TypeFlags.Byte, ColumnType = "bytea"},
                new {Flags = TypeFlags.Boolean, ColumnType = "boolean"},
                new {Flags = TypeFlags.Integer8, ColumnType = "smallint"},
                new {Flags = TypeFlags.Integer16, ColumnType = "smallint"},
                new {Flags = TypeFlags.Integer32, ColumnType = "integer"},
                new {Flags = TypeFlags.Integer64, ColumnType = "bigint"},
                new {Flags = TypeFlags.Decimal, ColumnType = "decimal({0},{1})"},
                new {Flags = TypeFlags.Double, ColumnType = "double precision"},
                new {Flags = TypeFlags.Single, ColumnType = "real"},
                new {Flags = TypeFlags.String, ColumnType = "varchar({0})"},
                new {Flags = TypeFlags.DateTime, ColumnType = "timestamp"}
            };


            string[] tableNameParts = schema.MappedName.Split('.');
            string tableSchemaName = tableNameParts.Length == 1 ? "dbo" : tableNameParts[0];
            string tableName = tableNameParts.Length == 1 ? tableNameParts[0] : tableNameParts[1];

            var existingColumns = dataProvider.ExecuteSqlReader("select * from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME=@name",
                QueryParameterCollection.FromObject(new { schema = tableSchemaName, name = tableName })).ToLookup(rec => rec["column_name"].ToString());

            var tableExists = dataProvider.ExecuteSqlReader("select count(*) from INFORMATION_SCHEMA.TABLES where TABLE_NAME=@name",
                                  QueryParameterCollection.FromObject(new { schema = tableSchemaName, name = tableName })).Select(rec => rec.First().Value.Convert<int>()).First() == 1;

            var parts = new List<string>();

            bool createNew = true;

            foreach (var field in schema.Fields)
            {
                var columnMapping = columnMappings.FirstOrDefault(mapping => field.FieldInfo.Inspector().Type.Inspector().Is(mapping.Flags));

                if (columnMapping == null)
                    continue;

                if (existingColumns.Contains(field.MappedName) && !recreateTable)
                {
                    createNew = false;
                    continue;
                }

                if (columnMapping.Flags == TypeFlags.String && field.ColumnSize == int.MaxValue)
                    columnMapping = new { columnMapping.Flags, ColumnType = longTextType };

                if (field.AutoIncrement)
                {
                    parts.Add($"{QuoteField(field.MappedName)} SERIAL");
                }
                else
                {

                    var part = $"{QuoteField(field.MappedName)} {string.Format(columnMapping.ColumnType, field.ColumnSize, field.ColumnScale)}";

                    if (!field.ColumnNullable || field.PrimaryKey)
                        part += " NOT";

                    part += " NULL";

                    parts.Add(part);
                }
                
            }

            if (parts.Any() && schema.PrimaryKeys.Length > 0)
            {
                parts.Add("PRIMARY KEY (" + string.Join(",", schema.PrimaryKeys.Select(pk => QuoteField(pk.MappedName))) + ")");
            }

            if (recreateTable && tableExists)
                dataProvider.ExecuteSql("DROP TABLE " + QuoteTable(schema.MappedName), null);

            if (parts.Any())
            {
                string sql = (createNew ? "CREATE TABLE " : "ALTER TABLE ") + QuoteTable(schema.MappedName);

                sql += createNew ? " (" : " ADD ";

                sql += string.Join(",", parts);

                if (createNew)
                    sql += ")";

                dataProvider.ExecuteSql(sql, null);
            }
        }

        public override string SqlFunction(Function function, params string[] parameters)
        {
            switch (function)
            {
                default:
                    return base.SqlFunction(function,parameters);
            }
        }

        public override bool SupportsInsertOrUpdate => true;
        public override bool RequiresAutoIncrementGetInSameStatement => true;
    }
}