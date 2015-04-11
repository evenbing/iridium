﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;


#if VELOX_SQLSERVER
namespace Velox.DB.Sql.SqlServer
#elif VELOX_MYSQL
namespace Velox.DB.Sql.MySql
#elif VELOX_SQLITE
namespace Velox.DB.Sql.Sqlite
#else
namespace Velox.DB.Sql
#endif
{
    public abstract class SqlAdoDataProvider<TConnection, TDialect> : SqlDataProvider<TDialect>
        where TConnection : DbConnection, new()
        where TDialect : SqlDialect, new()
    {
        private readonly string _connectionString;

        private readonly ThreadLocal<DbConnection> _localConnection = new ThreadLocal<DbConnection>();

        protected SqlAdoDataProvider(string connectionString)
        {
            _connectionString = connectionString;
        }

        private DbConnection Connection
        {
            get
            {
                var connection = _localConnection.Value;

                Debug.WriteLine("Fetching connection ({0})", connection);

                if (connection != null)
                {
                    if (connection.State == ConnectionState.Open)
                        return connection;

                    connection.Dispose();

                    ClearConnectionPool();
                }

                _localConnection.Value = CreateAndOpenConnection();

                return _localConnection.Value;
            }
        }

        protected virtual DbConnection CreateAndOpenConnection()
        {
            var connection = new TConnection() { ConnectionString = _connectionString };

            connection.Open();

            Debug.WriteLine("Creating connection");

            return connection;
        }

        public abstract void ClearConnectionPool();

        protected DbCommand CreateCommand(string sqlQuery, Dictionary<string, object> parameters)
        {
            DbCommand dbCommand = Connection.CreateCommand();

            dbCommand.CommandType = CommandType.Text;
            dbCommand.CommandText = sqlQuery;

            dbCommand.CommandText = Regex.Replace(sqlQuery, @"@(?<name>[a-z0-9A-Z_]+)", match => SqlDialect.CreateParameterExpression(match.Value.Substring(1)));

            if (parameters != null)
                foreach (var parameter in parameters)
                {
                    IDbDataParameter dataParameter = dbCommand.CreateParameter();

                    dataParameter.ParameterName = SqlDialect.CreateParameterExpression(parameter.Key);
                    dataParameter.Direction = ParameterDirection.Input;
                    dataParameter.Value = ConvertParameter(parameter.Value);

                    dbCommand.Parameters.Add(dataParameter);
                }

            return dbCommand;
        }

        protected virtual object ConvertParameter(object value)
        {
            if (value == null)
                return DBNull.Value;

            if (value is Enum)
                return Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), null);

            return value;
        }



        protected override IEnumerable<Dictionary<string, object>> ExecuteSqlReader(string sql, Dictionary<string, object> parameters = null)
        {
            Debug.WriteLine(string.Format("{0}", sql));

            List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();

            using (var cmd = CreateCommand(sql, parameters))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Dictionary<string, object> rec = new Dictionary<string, object>();

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string fieldName = reader.GetName(i);

                            if (reader.IsDBNull(i))
                                rec[fieldName] = null;
                            else
                                rec[fieldName] = reader.GetValue(i);
                        }

                        records.Add(rec);
                    }
                }
            }

            return records;
        }

        protected override int ExecuteSql(string sql, Dictionary<string, object> parameters = null)
        {
            Debug.WriteLine(string.Format("{0}", sql));

            using (var cmd = CreateCommand(sql, parameters))
            {
                return cmd.ExecuteNonQuery();
            }
        }

        public override int ExecuteSql(string sql, QueryParameterCollection parameters)
        {
            return ExecuteSql(sql, parameters == null ? null : parameters.AsDictionary());
        }

        public override IEnumerable<SerializedEntity> Query(string sql, QueryParameterCollection parameters)
        {
            return ExecuteSqlReader(sql, parameters.AsDictionary()).Select(rec => new SerializedEntity(rec));
        }

        public override object QueryScalar(string sql, QueryParameterCollection parameters)
        {
            var result = ExecuteSqlReader(sql, parameters.AsDictionary()).FirstOrDefault();

            if (result != null)
                return result.First().Value;
            else
                return null;
        }
    }
}