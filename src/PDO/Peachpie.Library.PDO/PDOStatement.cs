﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Pchp.Core;
using static Peachpie.Library.PDO.PDO;

namespace Peachpie.Library.PDO
{
    /// <summary>
    /// PDOStatement class
    /// </summary>
    /// <seealso cref="IPDOStatement" />
    [PhpType(PhpTypeAttribute.InheritName)]
    public class PDOStatement : IPDOStatement, IDisposable
    {
        private readonly PDO m_pdo;
        private readonly string m_stmt;
        private readonly PhpArray m_options;

        private readonly DbCommand m_cmd;
        private DbDataReader m_dr;
        private readonly Dictionary<PDO.PDO_ATTR, PhpValue> m_attributes = new Dictionary<PDO.PDO_ATTR, PhpValue>();
        private string[] m_dr_names;

        private bool m_positionalAttr = false;
        private bool m_namedAttr = false;
        private Dictionary<string, string> m_namedPlaceholders;
        private List<String> m_positionalPlaceholders;
         
        private PDO.PDO_FETCH m_fetchStyle = PDO.PDO_FETCH.FETCH_BOTH;
        public int FetchColNo { get; set; } = -1;
        public string FetchClassName { get; set; } = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="PDOStatement" /> class.
        /// </summary>
        /// <param name="pdo">The pdo.</param>
        /// <param name="statement">The statement.</param>
        /// <param name="driver_options">The driver options.</param>
        internal PDOStatement(PDO pdo, string statement, PhpArray driver_options)
        {
            this.m_pdo = pdo;
            this.m_stmt = statement;
            this.m_options = driver_options ?? PhpArray.Empty;

            this.m_cmd = pdo.CreateCommand(this.m_stmt);

            this.SetDefaultAttributes();
        }

        /// <summary>
        /// Empty ctor.
        /// </summary>
        protected PDOStatement()
        {
            m_pdo = null;
            m_stmt = null;
            m_options = PhpArray.Empty;
            m_cmd = null;
        }

        private static readonly Regex regName = new Regex(@"[\w_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Prepare the PDOStatement command.
        /// Set either positional, named or neither parameters mode.
        /// Create the parameters, add them to the command and prepare the command.
        /// </summary>
        /// <returns></returns>
        private bool PrepareStatement()
        {
            Debug.Assert(m_stmt != null && m_stmt.Length > 0);

            m_namedPlaceholders = new Dictionary<string, string>();
            m_positionalPlaceholders = new List<string>();
            m_namedAttr = false;
            m_positionalAttr = false;

            int pos = 0;
            var rewrittenQuery = new StringBuilder();

            // Go throught the text query and find either positional or named parameters
            while(pos < m_stmt.Length)
            {
                char currentChar = m_stmt[pos];
                string paramName = "";

                switch (currentChar)
                {
                    case '?':
                        if(m_namedAttr)
                        {
                            throw new PDOException("Mixing positional and named parameters not allowed. Use only '?' or ':name' pattern");
                        }

                        m_positionalAttr = true;

                        paramName = "@p" + m_positionalPlaceholders.Count();
                        m_positionalPlaceholders.Add(paramName);
                        rewrittenQuery.Append(paramName);

                        break;

                    case ':':
                        if(m_positionalAttr)
                        {
                            throw new PDOException("Mixing positional and named parameters not allowed.Use only '?' or ':name' pattern");
                        }

                        m_namedAttr = true;

                        var match= regName.Match(m_stmt, pos);
                        string param = match.Value;

                        paramName = "@" + param;
                        m_namedPlaceholders[param] = paramName;
                        rewrittenQuery.Append(paramName);

                        pos += param.Length; 

                        break;

                    case '"':
                        rewrittenQuery.Append(currentChar);
                        pos = SkipQuotedWord(m_stmt, rewrittenQuery, pos, '"');
                        break;

                    case '\'':
                        rewrittenQuery.Append(currentChar);
                        pos = SkipQuotedWord(m_stmt, rewrittenQuery, pos, '\'');
                        break;

                    default:
                        rewrittenQuery.Append(currentChar);
                        break;
                }
                pos++;
            }

            m_cmd.CommandText = rewrittenQuery.ToString();
            m_cmd.Parameters.Clear();

            if(m_positionalAttr)
            {
                foreach (var paramName in m_positionalPlaceholders.ToArray())
                {
                    var param = m_cmd.CreateParameter();
                    param.ParameterName = paramName;
                    m_cmd.Parameters.Add(param);
                }
            } else if(m_namedAttr)
            {
                foreach (var paramPair in m_namedPlaceholders)
                {
                    var param = m_cmd.CreateParameter();
                    param.ParameterName = paramPair.Value;
                    m_cmd.Parameters.Add(param);
                }
            }

            // Finalise the command preparation
            m_cmd.Prepare();

            return true;
        }

        private void SetAttributesType()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Skip the quoted part of query
        /// </summary>
        /// <param name="query">Textual query</param>
        /// <param name="rewrittenBuilder">StringBuilder for the rewritten query</param>
        /// <param name="pos">Current position in the query</param>
        /// <param name="quoteType">Quotational character used</param>
        /// <returns>New position in the query</returns>
        private int SkipQuotedWord(string query, StringBuilder rewrittenBuilder, int pos, char quoteType)
        {
            while(++pos < query.Length)
            {
                char currentChar = query[pos];
                rewrittenBuilder.Append(currentChar);

                if (currentChar == quoteType)
                    break;

                if (currentChar == '\\')
                    pos++;
            }
            return pos;
        }

        private void SetDefaultAttributes()
        {
            this.m_attributes[PDO.PDO_ATTR.ATTR_CURSOR] = (PhpValue)(int)PDO.PDO_CURSOR.CURSOR_FWDONLY;
        }

        /// <inheritDoc />
        void IDisposable.Dispose()
        {
            this.m_dr?.Dispose();
            this.m_cmd.Dispose();
        }

        private void OpenReader()
        {
            if (this.m_dr == null)
            {
                PDO.PDO_CURSOR cursor = (PDO.PDO_CURSOR)this.m_attributes[PDO.PDO_ATTR.ATTR_CURSOR].ToLong();
                this.m_dr = this.m_pdo.Driver.OpenReader(this.m_pdo, this.m_cmd, cursor);
                switch (cursor)
                {
                    case PDO.PDO_CURSOR.CURSOR_FWDONLY:
                        this.m_dr = this.m_cmd.ExecuteReader();
                        break;
                    case PDO.PDO_CURSOR.CURSOR_SCROLL:
                        this.m_dr = this.m_cmd.ExecuteReader();
                        break;
                    default:
                        throw new InvalidProgramException();
                }
                this.m_dr_names = new string[this.m_dr.FieldCount];
                for (int i = 0; i < this.m_dr_names.Length; i++)
                {
                    this.m_dr_names[i] = this.m_dr.GetName(i);
                }
            }
        }

        /// <inheritDoc />
        public bool bindColumn(PhpValue colum, ref PhpValue param, int? type = default(int?), int? maxlen = default(int?), PhpValue? driverdata = default(PhpValue?))
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool bindParam(PhpValue parameter, ref PhpValue variable, int data_type = 2, int? length = default(int?), PhpValue? driver_options = default(PhpValue?))
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool bindValue(PhpValue parameter, PhpValue value, int data_type = 2)
        {
            Debug.Assert(this.m_cmd != null);

            string key = parameter.String;

            if (key.Length > 0 && key[0] == ':')
            {
                key = key.Substring(1);
            }

            IDataParameter param;

            param = this.m_cmd.Parameters[key];

            param.Value = value.AsString();




            return true;
        }

        /// <inheritDoc />
        public bool bindValues(PhpValue parameter, PhpValue value, int data_type = 2)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool closeCursor()
        {
            if (this.m_dr != null)
            {
                ((IDisposable)this.m_dr).Dispose();
                this.m_dr = null;
                return true;
            }
            return false;
        }

        /// <inheritDoc />
        public int columnCount()
        {
            if (this.m_dr == null)
            {
                return 0;
            }

            return this.m_dr.FieldCount;
        }

        /// <inheritDoc />
        public void debugDumpParams()
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public string errorCode()
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public PhpArray errorInfo()
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool execute(PhpArray input_parameters = null)
        {
            if (input_parameters != null)
            {
                foreach (var param in input_parameters)
                {
                    m_cmd.Parameters.Add(param);
                }
            }

            m_dr = null;
            try
            {
                m_dr = m_cmd.ExecuteReader();
            } catch(Exception e)
            {
                throw new PDOException("Query could not be executed; \n" + e.Message);
            }
            

            return true;
        }

        /// <inheritDoc />
        public PhpValue fetch(int fetch_style = -1, int cursor_orientation = default(int), int cursor_offet = 0)
        {
            this.m_pdo.ClearError();
            try
            {
                PDO.PDO_FETCH style = this.m_fetchStyle;

                if (fetch_style != -1 && Enum.IsDefined(typeof(PDO.PDO_FETCH), fetch_style))
                {
                    style = (PDO.PDO_FETCH)fetch_style;
                }
                PDO.PDO_FETCH_ORI ori = PDO.PDO_FETCH_ORI.FETCH_ORI_NEXT;
                if (Enum.IsDefined(typeof(PDO.PDO_FETCH_ORI), cursor_orientation))
                {
                    ori = (PDO.PDO_FETCH_ORI)cursor_orientation;
                }

                switch (ori)
                {
                    case PDO.PDO_FETCH_ORI.FETCH_ORI_NEXT:
                        break;
                    default:
                        throw new NotSupportedException();
                }

                if (!this.m_dr.Read())
                    return PhpValue.False;

                // Get the column schema, if possible, for the associative fetch
                if(this.m_dr_names == null)
                {
                    this.m_dr_names = new string[m_dr.FieldCount];

                    if (this.m_dr.CanGetColumnSchema()) {
                        var columnSchema = this.m_dr.GetColumnSchema();

                        for (int i = 0; i < m_dr.FieldCount; i++)
                        {
                            this.m_dr_names[i] = columnSchema[i].ColumnName;
                        }
                    }
                }

                switch (style)
                {
                    case PDO.PDO_FETCH.FETCH_OBJ:
                        return this.ReadObj();
                    case PDO.PDO_FETCH.FETCH_ASSOC:
                        return PhpValue.Create(this.ReadArray(true, false));
                    case PDO.PDO_FETCH.FETCH_BOTH:
                    case PDO.PDO_FETCH.FETCH_USE_DEFAULT:
                        return PhpValue.Create(this.ReadArray(true, true));
                    case PDO.PDO_FETCH.FETCH_NUM:
                        return PhpValue.Create(this.ReadArray(false, true));
                    case PDO.PDO_FETCH.FETCH_COLUMN:
                        return this.ReadArray(false, true)[FetchColNo].GetValue();
                    case PDO.PDO_FETCH.FETCH_CLASS:
                    default:
                        throw new NotImplementedException();
                }
            }
            catch (System.Exception ex)
            {
                this.m_pdo.HandleError(ex);
                return PhpValue.False;
            }
        }

        private PhpValue ReadObj()
        {
            return PhpValue.FromClass(this.ReadArray(true, false).ToClass());
        }


        private PhpArray ReadArray(bool assoc, bool num)
        {
            PhpArray arr = new PhpArray();
            for (int i = 0; i < this.m_dr.FieldCount; i++)
            {
                if (this.m_dr.IsDBNull(i))
                {
                    if (assoc)
                        arr.Add(this.m_dr_names[i], PhpValue.Null);
                    if (num)
                        arr.Add(i, PhpValue.Null);
                }
                else
                {
                    var value = PhpValue.FromClr(this.m_dr.GetValue(i));
                    if (assoc)
                        arr.Add(this.m_dr_names[i], value);
                    if (num)
                        arr.Add(i, value);
                }
            }
            return arr;
        }

        /// <inheritDoc />
        public PhpArray fetchAll(int? fetch_style = default(int?), PhpValue? fetch_argument = default(PhpValue?), PhpArray ctor_args = null)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public PhpValue fetchColumn(int column_number = 0)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public PhpValue fetchObject(string class_name = "stdClass", PhpArray ctor_args = null)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public PhpValue getAttribute(int attribute)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        [return: CastToFalse]
        public PhpArray getColumnMeta(int column)
        {
            if (this.m_dr == null)
            {
                return null;
            }

            if (column < 0 || column >= this.m_dr.FieldCount)
                return null;

            PhpArray meta = new PhpArray();
            meta.Add("native_type", this.m_dr.GetFieldType(column).FullName);
            meta.Add("driver:decl_type", this.m_dr.GetDataTypeName(column));
            //meta.Add("flags", PhpValue.Null);
            meta.Add("name", this.m_dr_names[column]);
            //meta.Add("table", PhpValue.Null);
            //meta.Add("len", PhpValue.Null);
            //meta.Add("prevision", PhpValue.Null);
            //meta.Add("pdo_type", (int)PDO.PARAM.PARAM_NULL);
            return meta;
        }

        /// <inheritDoc />
        public bool nextRowset()
        {
            if (this.m_dr == null)
            {
                return false;
            }
            if (this.m_dr.NextResult())
            {
                this.m_dr_names = new string[this.m_dr.FieldCount];
                for (int i = 0; i < this.m_dr_names.Length; i++)
                {
                    this.m_dr_names[i] = this.m_dr.GetName(i);
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <inheritDoc />
        public int rowCount()
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool setAttribute(int attribute, PhpValue value)
        {
            throw new NotImplementedException();
        }

        /// <inheritDoc />
        public bool setFetchMode(params PhpValue[] args)
        {
            PDO_FETCH fetch = PDO_FETCH.FETCH_USE_DEFAULT;

            PhpValue fetchMode = args[0];
            if (fetchMode.IsInteger())
            {
                int value = (int)fetchMode.Long;
                if (Enum.IsDefined(typeof(PDO_FETCH), value))
                {
                    fetch = (PDO_FETCH)value;

                    this.m_fetchStyle = fetch;
                }
                else
                {
                    throw new PDOException("Given PDO_FETCH constant is not implemented.");
                }
            }

            if (fetch == PDO_FETCH.FETCH_COLUMN)
            {
                int colNo = -1;
                if (args.Length > 1)
                {
                    if (args[1].IsInteger())
                    {
                        colNo = (int)args[1].ToLong();
                        this.FetchColNo = colNo;
                    }
                    else
                    {
                        throw new PDOException("General error: colno must be an integer");
                    }
                }
                else
                {
                    throw new PDOException("General error: fetch mode requires the colno argument");

                    //TODO what to do if missing parameter ?
                    //fetch = PDO_FETCH.FETCH_USE_DEFAULT;
                }
            }
            string className = null;
            PhpArray ctorArgs = null;
            if (fetch == PDO_FETCH.FETCH_CLASS)
            {
                if (args.Length > 2)
                {
                    className = args[1].ToStringOrNull();
                    this.FetchClassName = className;

                    if (args.Length > 3)
                    {
                        ctorArgs = args[2].ArrayOrNull();
                    }
                }
                else
                {
                    throw new PDOException("General error: fetch mode requires the classname argument.");

                    //TODO what to do if missing parameter ?
                    //fetch = PDO_FETCH.FETCH_USE_DEFAULT;
                }
            }
            //TODO: FETCH_OBJ does not require additional parameters

            /*PhpValue? fetchObject = null;
            if (fetch == PDO_FETCH.FETCH_OBJ)
            {
                if (args.Length > 2)
                {
                    fetchObject = args[1];
                    if (fetchObject.Value.IsNull)
                    {
                        //TODO passed object is null
                    }
                }
                else
                {
                    //TODO what to do if missing parameter ?
                    fetch = PDO_FETCH.FETCH_USE_DEFAULT;
                }
            }*/

            return true;
        }
    }
}
