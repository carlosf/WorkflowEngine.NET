﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using OptimaJet.Workflow.Core;
using OptimaJet.Workflow.Core.Fault;
using OptimaJet.Workflow.Core.Generator;
using OptimaJet.Workflow.Core.Model;
using OptimaJet.Workflow.Core.Persistence;
using OptimaJet.Workflow.Core.Runtime;

namespace OptimaJet.Workflow.DbPersistence
{
    public class MSSQLProvider : IPersistenceProvider, ISchemePersistenceProvider<XElement>, IWorkflowGenerator<XElement>
    {
        public string ConnectionString { get; set; }
        private WorkflowRuntime _runtime;
        public void Init(WorkflowRuntime runtime)
        {
            _runtime = runtime;
        }

        public MSSQLProvider(string connectionString, string schema = "dbo" )
        {
            ConnectionString = connectionString;
            DbObject.SchemaName = schema;
        }

        #region IPersistenceProvider
        public void InitializeProcess(ProcessInstance processInstance)
        {
            using(SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var oldProcess = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);
                if (oldProcess != null)
                {
                    throw new ProcessAlredyExistsException();
                }
                var newProcess = new WorkflowProcessInstance
                {
                    Id = processInstance.ProcessId,
                    SchemeId = processInstance.SchemeId,
                    ActivityName = processInstance.ProcessScheme.InitialActivity.Name,
                    StateName = processInstance.ProcessScheme.InitialActivity.State,
                    RootProcessId = processInstance.RootProcessId,
                    ParentProcessId = processInstance.ParentProcessId
                };
                newProcess.Insert(connection);
            }
        }

        public void BindProcessToNewScheme(ProcessInstance processInstance)
        {
            BindProcessToNewScheme(processInstance, false);
        }

        public void BindProcessToNewScheme(ProcessInstance processInstance, bool resetIsDeterminingParametersChanged)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var oldProcess = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);
                if (oldProcess == null)
                    throw new ProcessNotFoundException();

                oldProcess.SchemeId = processInstance.SchemeId;
                if (resetIsDeterminingParametersChanged)
                    oldProcess.IsDeterminingParametersChanged = false;
                oldProcess.Update(connection);
            }
        }

        public void FillProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void FillPersistedProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetPersistedProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void FillSystemProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetSystemProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void SavePersistenceParameters(ProcessInstance processInstance)
        {
            var parametersToPersistList =
                processInstance.ProcessParameters.Where(ptp => ptp.Purpose == ParameterPurpose.Persistence)
                    .Select(
                        ptp => new {Parameter = ptp, SerializedValue = _runtime.SerializeParameter(ptp.Value, ptp.Type)})
                    .ToList();
            
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var persistedParameters = WorkflowProcessInstancePersistence.SelectByProcessId(connection, processInstance.ProcessId).ToList();

                foreach (var parameterDefinitionWithValue in parametersToPersistList)
                {
                    var persistence =
                        persistedParameters.SingleOrDefault(
                            pp => pp.ParameterName == parameterDefinitionWithValue.Parameter.Name);
                    {
                        if (persistence == null)
                        {
                            if (parameterDefinitionWithValue.SerializedValue != null)
                            {
                                persistence = new WorkflowProcessInstancePersistence()
                                {
                                    Id = Guid.NewGuid(),
                                    ProcessId = processInstance.ProcessId,
                                    ParameterName = parameterDefinitionWithValue.Parameter.Name,
                                    Value = parameterDefinitionWithValue.SerializedValue
                                };
                                persistence.Insert(connection);
                            }
                        }
                        else
                        {
                            if (parameterDefinitionWithValue.SerializedValue != null)
                            {
                                persistence.Value = parameterDefinitionWithValue.SerializedValue;
                                persistence.Update(connection);
                            }
                            else
                                WorkflowProcessInstancePersistence.Delete(connection, persistence.Id);
                        }
                    }
                }
            }
        }

        public void SetWorkflowIniialized(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Initialized, true);
        }

        public void SetWorkflowIdled(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Idled);
        }

        public void SetWorkflowRunning(ProcessInstance processInstance)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var instanceStatus = WorkflowProcessInstanceStatus.SelectByKey(connection, processInstance.ProcessId);

                if (instanceStatus == null)
                    throw new StatusNotDefinedException();

                if (instanceStatus.Status == ProcessStatus.Running.Id)
                    throw new ImpossibleToSetStatusException();

                var oldLock = instanceStatus.Lock;

                instanceStatus.Lock = Guid.NewGuid();
                instanceStatus.Status = ProcessStatus.Running.Id;
                
                var cnt = WorkflowProcessInstanceStatus.ChangeStatus(connection, instanceStatus, oldLock);

                if (cnt != 1)
                    throw new ImpossibleToSetStatusException();
            }
        }

        public void SetWorkflowFinalized(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Finalized);
        }

#pragma warning disable 612
        public void SetWorkflowTerminated(ProcessInstance processInstance, ErrorLevel level, string errorMessage)
#pragma warning restore 612
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Terminated);
        }

        public void ResetWorkflowRunning()
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessInstanceStatus.MassChangeStatus(connection, ProcessStatus.Running.Id, ProcessStatus.Idled.Id);
            }
        }
      
        public void UpdatePersistenceState(ProcessInstance processInstance, TransitionDefinition transition)
        {
            var paramIdentityId = processInstance.GetParameter(DefaultDefinitions.ParameterIdentityId.Name);
            var paramImpIdentityId = processInstance.GetParameter(DefaultDefinitions.ParameterImpersonatedIdentityId.Name);

            var identityId = paramIdentityId == null ? string.Empty : (string)paramIdentityId.Value;
            var impIdentityId = paramImpIdentityId == null ? identityId : (string)paramImpIdentityId.Value;

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessInstance inst = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);
                
                if (inst != null)
                {
                    if (!string.IsNullOrEmpty(transition.To.State))
                        inst.StateName = transition.To.State;

                    inst.ActivityName = transition.To.Name;
                    inst.PreviousActivity = transition.From.Name;

                    if (!string.IsNullOrEmpty(transition.From.State))
                        inst.PreviousState = transition.From.State;

                    if (transition.Classifier == TransitionClassifier.Direct)
                    {
                        inst.PreviousActivityForDirect = transition.From.Name;

                        if (!string.IsNullOrEmpty(transition.From.State))
                            inst.PreviousStateForDirect = transition.From.State;
                    }
                    else if (transition.Classifier == TransitionClassifier.Reverse)
                    {
                        inst.PreviousActivityForReverse = transition.From.Name;

                        if (!string.IsNullOrEmpty(transition.From.State))
                            inst.PreviousStateForReverse = transition.From.State;
                    }

                    inst.ParentProcessId = processInstance.ParentProcessId;
                    inst.RootProcessId = processInstance.RootProcessId;

                    inst.Update(connection);
                }

                var history = new WorkflowProcessTransitionHistory()
                {
                    ActorIdentityId = impIdentityId,
                    ExecutorIdentityId = identityId,
                    Id = Guid.NewGuid(),
                    IsFinalised = false,
                    ProcessId = processInstance.ProcessId,
                    FromActivityName = transition.From.Name,
                    FromStateName = transition.From.State,
                    ToActivityName = transition.To.Name,
                    ToStateName = transition.To.State,
                    TransitionClassifier =
                        transition.Classifier.ToString(),
                    TransitionTime = _runtime.RuntimeDateTimeNow,
                    TriggerName = string.IsNullOrEmpty(processInstance.ExecutedTimer) ? processInstance.CurrentCommand : processInstance.ExecutedTimer
                };
                history.Insert(connection);
            }
        }

        public bool IsProcessExists(Guid processId)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                return WorkflowProcessInstance.SelectByKey(connection, processId) != null;
            }
        }

        public ProcessStatus GetInstanceStatus(Guid processId)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var instance = WorkflowProcessInstanceStatus.SelectByKey(connection, processId);
                if (instance == null)
                    return ProcessStatus.NotFound;
                var status = ProcessStatus.All.SingleOrDefault(ins => ins.Id == instance.Status);
                if (status == null)
                    return ProcessStatus.Unknown;
                return status;
            }
        }


        private void SetCustomStatus(Guid processId, ProcessStatus status, bool createIfnotDefined = false)
        {
            using(SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var instanceStatus = WorkflowProcessInstanceStatus.SelectByKey(connection, processId);
                if(instanceStatus == null)
                {
                    if(!createIfnotDefined)
                        throw new StatusNotDefinedException();

                    instanceStatus = new WorkflowProcessInstanceStatus()
                    {
                        Id = processId,
                        Lock = Guid.NewGuid(),
                        Status = ProcessStatus.Initialized.Id
                    };

                    instanceStatus.Insert(connection);
                }
                else
                {
                    var oldLock = instanceStatus.Lock;

                    instanceStatus.Status = status.Id;
                    instanceStatus.Lock = Guid.NewGuid();

                    var cnt = WorkflowProcessInstanceStatus.ChangeStatus(connection, instanceStatus, oldLock);

                    if (cnt != 1)
                        throw new ImpossibleToSetStatusException();
                }

            }
        }

        private IEnumerable<ParameterDefinitionWithValue> GetProcessParameters(Guid processId, ProcessDefinition processDefinition)
        {
            var parameters = new List<ParameterDefinitionWithValue>(processDefinition.Parameters.Count());
            parameters.AddRange(GetPersistedProcessParameters(processId, processDefinition));
            parameters.AddRange(GetSystemProcessParameters(processId, processDefinition));
            return parameters;
        }

        private IEnumerable<ParameterDefinitionWithValue> GetSystemProcessParameters(Guid processId,
            ProcessDefinition processDefinition)
        {
            var processInstance = GetProcessInstance(processId);

            var systemParameters =
                processDefinition.Parameters.Where(p => p.Purpose == ParameterPurpose.System).ToList();

            var parameters = new List<ParameterDefinitionWithValue>(systemParameters.Count())
            {
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterProcessId.Name),
                    processId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousState.Name),
                     processInstance.PreviousState),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterCurrentState.Name),
                     processInstance.StateName),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousStateForDirect.Name),
                     processInstance.PreviousStateForDirect),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousStateForReverse.Name),
                     processInstance.PreviousStateForReverse),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivity.Name),
                     processInstance.PreviousActivity),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterCurrentActivity.Name),
                     processInstance.ActivityName),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivityForDirect.Name),
                     processInstance.PreviousActivityForDirect),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivityForReverse.Name),
                     processInstance.PreviousActivityForReverse),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterSchemeCode.Name),
                     processDefinition.Name),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterSchemeId.Name),
                     processInstance.SchemeId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterIsPreExecution.Name),
                    false),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterParentProcessId.Name),
                     processInstance.ParentProcessId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterRootProcessId.Name),
                     processInstance.RootProcessId),
            };
            return parameters;
        }

        private IEnumerable<ParameterDefinitionWithValue> GetPersistedProcessParameters(Guid processId, ProcessDefinition processDefinition)
        {
            var persistenceParameters = processDefinition.PersistenceParameters.ToList();
            var parameters = new List<ParameterDefinitionWithValue>(persistenceParameters.Count());

            List<WorkflowProcessInstancePersistence> persistedParameters;

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                persistedParameters = WorkflowProcessInstancePersistence.SelectByProcessId(connection, processId).ToList();
            }

            foreach (var persistedParameter in persistedParameters)
            {
                var parameterDefinition = persistenceParameters.FirstOrDefault(p => p.Name == persistedParameter.ParameterName);
                if (parameterDefinition == null)
                    parameterDefinition = ParameterDefinition.Create(persistedParameter.ParameterName, "System.String", ParameterPurpose.Persistence.ToString(), null);

                parameters.Add(ParameterDefinition.Create(parameterDefinition, _runtime.DeserializeParameter(persistedParameter.Value, parameterDefinition.Type)));
            }

            return parameters;
        }


        private WorkflowProcessInstance GetProcessInstance(Guid processId)
        {
            using(SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var processInstance = WorkflowProcessInstance.SelectByKey(connection, processId);
                if (processInstance == null)
                    throw new ProcessNotFoundException();
                return processInstance;
            }
        }

        public void DeleteProcess(Guid[] processIds)
        {
            foreach (var processId in processIds)
                DeleteProcess(processId);
        }

        public void SaveGlobalParameter<T>(string type, string name, T value)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var parameter = WorkflowGlobalParameter.SelectByTypeAndName(connection,type, name).FirstOrDefault();

                if (parameter == null)
                {
                    parameter = new WorkflowGlobalParameter()
                    {
                        Id = Guid.NewGuid(),
                        Type = type,
                        Name = name,
                        Value = JsonConvert.SerializeObject(value)
                    };

                    parameter.Insert(connection);
                }
                else
                {
                    parameter.Value = JsonConvert.SerializeObject(value);

                    parameter.Update(connection);
                }
            }
        }

        public T LoadGlobalParameter<T>(string type, string name)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var parameter = WorkflowGlobalParameter.SelectByTypeAndName(connection, type, name).FirstOrDefault();

                if (parameter == null)
                    return default (T);

                return JsonConvert.DeserializeObject<T>(parameter.Value);
            }

        }

        public List<T> LoadGlobalParameters<T>(string type)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var parameters = WorkflowGlobalParameter.SelectByTypeAndName(connection, type);

                return parameters.Select(p => JsonConvert.DeserializeObject<T>(p.Value)).ToList();
            }
        }

        public void DeleteGlobalParameters(string type, string name = null)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowGlobalParameter.DeleteByTypeAndName(connection, type, name);
            }
        }

        public void DeleteProcess(Guid processId)
        {
            using(SqlConnection connection = new SqlConnection(ConnectionString))
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    WorkflowProcessInstance.Delete(connection, processId, transaction);
                    WorkflowProcessInstanceStatus.Delete(connection, processId, transaction);
                    WorkflowProcessInstancePersistence.DeleteByProcessId(connection, processId, transaction);
                    WorkflowProcessTransitionHistory.DeleteByProcessId(connection, processId, transaction);
                    WorkflowProcessTimer.DeleteByProcessId(connection, processId, null, transaction);
                    transaction.Commit();
                }

            }
        }

        public void RegisterTimer(Guid processId, string name, DateTime nextExecutionDateTime, bool notOverrideIfExists)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var timer = WorkflowProcessTimer.SelectByProcessIdAndName(connection, processId, name);
                if (timer == null)
                {
                    timer = new WorkflowProcessTimer
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        NextExecutionDateTime = nextExecutionDateTime,
                        ProcessId = processId,
                        Ignore = false
                    };

                    timer.Insert(connection);
                }
                else if (!notOverrideIfExists)
                {
                    timer.NextExecutionDateTime = nextExecutionDateTime;
                    timer.Update(connection);
                }
            }
        }

        public void ClearTimers(Guid processId, List<string> timersIgnoreList)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.DeleteByProcessId(connection, processId, timersIgnoreList);
            }
        }

        public void ClearTimersIgnore()
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.ClearTimersIgnore(connection);
            }
        }

        public void ClearTimer(Guid timerId)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.Delete(connection, timerId);
            }
        }

        public DateTime? GetCloseExecutionDateTime()
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var timer = WorkflowProcessTimer.GetCloseExecutionTimer(connection);
                if (timer == null)
                    return null;

                return timer.NextExecutionDateTime;
            }
        }

        public List<TimerToExecute> GetTimersToExecute()
        {
            var now = _runtime.RuntimeDateTimeNow;

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var timers = WorkflowProcessTimer.GetTimersToExecute(connection, now);
                WorkflowProcessTimer.SetIgnore(connection, timers);

                return timers.Select(t => new TimerToExecute() { Name = t.Name, ProcessId = t.ProcessId, TimerId = t.Id }).ToList();
            }
        }
        #endregion

        #region ISchemePersistenceProvider
        public SchemeDefinition<XElement> GetProcessSchemeByProcessId(Guid processId)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var processInstance = WorkflowProcessInstance.SelectByKey(connection, processId);
                if (processInstance == null)
                    throw new ProcessNotFoundException();

                if (!processInstance.SchemeId.HasValue)
                    throw new SchemeNotFoundException();

                var schemeDefinition = GetProcessSchemeBySchemeId(processInstance.SchemeId.Value);
                schemeDefinition.IsDeterminingParametersChanged = processInstance.IsDeterminingParametersChanged;
                return schemeDefinition;
            }
        }

        public SchemeDefinition<XElement> GetProcessSchemeBySchemeId(Guid schemeId)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessScheme processScheme = WorkflowProcessScheme.SelectByKey(connection, schemeId);

                if (processScheme == null || string.IsNullOrEmpty(processScheme.Scheme))
                    throw new SchemeNotFoundException();

                return ConvertToSchemeDefinition(processScheme);
            }
        }
        
        public SchemeDefinition<XElement> GetProcessSchemeWithParameters(string schemeCode, string definingParameters,
            Guid? rootSchemeId, bool ignoreObsolete)
        {
            IEnumerable<WorkflowProcessScheme> processSchemes;
            var hash = HashHelper.GenerateStringHash(definingParameters);

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                processSchemes =
                    WorkflowProcessScheme.Select(connection, schemeCode, hash, ignoreObsolete ? false : (bool?) null,
                        rootSchemeId);
            }

            if (!processSchemes.Any())
                throw new SchemeNotFoundException();

            if (processSchemes.Count() == 1)
            {
                var scheme = processSchemes.First();
                return ConvertToSchemeDefinition(scheme);
            }

            foreach (var processScheme in processSchemes.Where(processScheme => processScheme.DefiningParameters == definingParameters))
            {
                return ConvertToSchemeDefinition(processScheme);
            }

            throw new SchemeNotFoundException();
        }

        public void SetSchemeIsObsolete(string schemeCode, IDictionary<string, object> parameters)
        {
            var definingParameters = DefiningParametersSerializer.Serialize(parameters);
            var definingParametersHash = HashHelper.GenerateStringHash(definingParameters);

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessScheme.SetObsolete(connection, schemeCode, definingParametersHash);
            }
        }

        public void SetSchemeIsObsolete(string schemeCode)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowProcessScheme.SetObsolete(connection, schemeCode);
            }
        }

        public void SaveScheme(SchemeDefinition<XElement> scheme)
        {
            var definingParameters = scheme.DefiningParameters;
            var definingParametersHash = HashHelper.GenerateStringHash(definingParameters);


            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                var oldSchemes = WorkflowProcessScheme.Select(connection, scheme.SchemeCode, scheme.DefiningParameters,
                    scheme.IsObsolete, scheme.RootSchemeId);

                if (oldSchemes.Any())
                {
                    if (oldSchemes.Any(oldScheme => oldScheme.DefiningParameters == definingParameters))
                    {
                        throw new SchemeAlredyExistsException();
                    }
                }

                var newProcessScheme = new WorkflowProcessScheme
                {
                    Id = scheme.Id,
                    DefiningParameters = definingParameters,
                    DefiningParametersHash = definingParametersHash,
                    Scheme = scheme.Scheme.ToString(),
                    SchemeCode = scheme.SchemeCode,
                    RootSchemeCode = scheme.RootSchemeCode,
                    RootSchemeId = scheme.RootSchemeId,
                    AllowedActivities = JsonConvert.SerializeObject(scheme.AllowedActivities),
                    StartingTransition = scheme.StartingTransition
                };

                newProcessScheme.Insert(connection);
            }
        }

        public void SaveScheme(string schemaCode, string scheme)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowScheme wfScheme = WorkflowScheme.SelectByKey(connection, schemaCode);
                if (wfScheme == null)
                {
                    wfScheme = new WorkflowScheme();
                    wfScheme.Code = schemaCode;
                    wfScheme.Scheme = scheme;
                    wfScheme.Insert(connection);
                }
                else
                {
                    wfScheme.Scheme = scheme;
                    wfScheme.Update(connection);
                }

            }
        }

       

        public XElement GetScheme(string code)
        {
            using(SqlConnection connection = new SqlConnection(ConnectionString))
            {
                WorkflowScheme scheme = WorkflowScheme.SelectByKey(connection, code);
                if (scheme == null || string.IsNullOrEmpty(scheme.Scheme))
                    throw new SchemeNotFoundException();

                return XElement.Parse(scheme.Scheme);
            }
        }

        #endregion

        #region IWorkflowGenerator
        public XElement Generate(string schemeCode, Guid schemeId, IDictionary<string, object> parameters)
        {
            if (parameters.Count > 0)
                throw new InvalidOperationException("Parameters not supported");

            return GetScheme(schemeCode);
        }
        #endregion

        private SchemeDefinition<XElement> ConvertToSchemeDefinition(WorkflowProcessScheme workflowProcessScheme)
        {
            return new SchemeDefinition<XElement>(workflowProcessScheme.Id, workflowProcessScheme.RootSchemeId,
                workflowProcessScheme.SchemeCode, workflowProcessScheme.RootSchemeCode,
                XElement.Parse(workflowProcessScheme.Scheme), workflowProcessScheme.IsObsolete, false,
                JsonConvert.DeserializeObject<List<string>>(workflowProcessScheme.AllowedActivities ?? "null"),
                workflowProcessScheme.StartingTransition,
                workflowProcessScheme.DefiningParameters);
        }

    }
}