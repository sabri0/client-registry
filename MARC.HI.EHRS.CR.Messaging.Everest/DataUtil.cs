﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MARC.HI.EHRS.SVC.Core.Services;
using MARC.HI.EHRS.SVC.Core.Exceptions;
using MARC.Everest.Connectors;
using MARC.HI.EHRS.CR.Core.ComponentModel;
using MARC.HI.EHRS.SVC.Core.DataTypes;
using System.Data;
using System.Data.Common;
using System.ComponentModel;
using System.Xml.Serialization;
using System.IO;
using System.Threading;
using MARC.Everest.Interfaces;
using System.Xml;
using System.Runtime.Serialization.Formatters.Binary;
using MARC.HI.EHRS.SVC.Core;
using MARC.HI.EHRS.SVC.PolicyEnforcement;
using MARC.HI.EHRS.SVC.Core.ComponentModel;
using MARC.HI.EHRS.SVC.Core.ComponentModel.Components;
using MARC.HI.EHRS.SVC.DecisionSupport;
using MARC.HI.EHRS.SVC.Core.Issues;


namespace MARC.HI.EHRS.CR.Messaging.Everest
{
    /// <summary>
    /// Data utilities wrap the interaction with the data persistence
    /// </summary>
    public class DataUtil : IUsesHostContext
    {

        /// <summary>
        /// The host context
        /// </summary>
        private HostContext m_context;

        // The system configuration service
        private ISystemConfigurationService m_configService;
        // The policy enforcement service
        private IPolicyEnforcementService m_policyService;
        // The auditor service
        private IAuditorService m_auditorService;
        // The document registration service
        private IDataRegistrationService m_docRegService;
        // The query service
        private IQueryPersistenceService m_queryService;
        // The decision service
        private IDecisionSupportService m_decisionService;
        // Data persistence service
        private IDataPersistenceService m_persistenceService;
        // localization service
        private ILocalizationService m_localeService;

        /// <summary>
        /// Sync lock
        /// </summary>
        private object syncLock = new object();

        /// <summary>
        /// Query result data
        /// </summary>
        public struct QueryResultData
        {

            /// <summary>
            /// Identifies the first record number that is to be returned in the set
            /// </summary>
            public int StartRecordNumber { get; set; }

            /// <summary>
            /// Gets or sets the identifier of the query the result set is for
            /// </summary>
            public Guid QueryId { get; set; }
            /// <summary>
            /// Gets or sets the results for the query
            /// </summary>
            public RegistrationEvent[] Results { get; set; }
            /// <summary>
            /// Gets or sets the total results for the query
            /// </summary>
            public int TotalResults { get; set; }
            /// <summary>
            /// Empty result
            /// </summary>
            public static QueryResultData Empty = new QueryResultData()
                {
                    Results = new RegistrationEvent[] { }
                };
        }

        /// <summary>
        /// Query data structure
        /// </summary>
        [XmlRoot("qd")]
        public struct QueryData
        {
            // Target (filter) identifiers for clients
            private List<DomainIdentifier> m_targetIds;


            /// <summary>
            /// True if the query is a summary query
            /// </summary>
            [XmlAttribute]
            public bool IsSummary { get; set; }

            
            /// <summary>
            /// Gets or sets the query id for the query 
            /// </summary>
            [XmlIgnore]
            public Guid QueryId { get; set; }
            /// <summary>
            /// Gets or sets the originator of the request
            /// </summary>
            [XmlAttribute("orgn")]
            public string Originator { get; set; }
            /// <summary>
            /// If true, include notes in the query results
            /// </summary>
            [XmlAttribute("nt")]
            public bool IncludeNotes { get; set; }
            /// <summary>
            /// If true, include history in the query results
            /// </summary>
            [XmlAttribute("hst")]
            public bool IncludeHistory { get; set; }
            /// <summary>
            /// Specifies the maximum number of query results to return fro mthe ffunction
            /// </summary>
            [XmlAttribute("qty")]
            public int Quantity { get; set; }
            /// <summary>
            /// Represents the original query component that is being used to query
            /// </summary>
            [XmlIgnore]
            public RegistrationEvent QueryRequest { get; set; }
            /// <summary>
            /// The minimum degree of match
            /// </summary>
            [XmlAttribute("minDegreeMatch")]
            public float MinimumDegreeMatch { get; set; }
            /// <summary>
            /// Matching algorithms
            /// </summary>
            [XmlAttribute("matchAlgorithm")]
            public MatchAlgorithm MatchingAlgorithm { get; set; }
            /// <summary>
            /// Original Request
            /// </summary>
            [XmlAttribute("originalConvo")]
            public string OriginalMessageQueryId { get; set; }
            
            /// <summary>
            /// Record Ids to be fetched
            /// </summary>
            [XmlIgnore]
            public List<VersionedDomainIdentifier> RecordIds { get; set; }

            /// <summary>
            /// Represent the QD as string
            /// </summary>
            public override string ToString()
            {
                StringWriter sb = new StringWriter();
                XmlSerializer xs = new XmlSerializer(this.GetType());
                xs.Serialize(sb, this);
                return sb.ToString();
            }

            /// <summary>
            /// Parse XML from the string
            /// </summary>
            internal static QueryData ParseXml(string p)
            {
                StringReader sr = new StringReader(p);
                XmlSerializer xsz = new XmlSerializer(typeof(QueryData));
                QueryData retVal = (QueryData)xsz.Deserialize(sr);
                sr.Close();
                return retVal;
            }
        }

        #region IUsesHostContext Members

        /// <summary>
        /// Gets or sets the host context
        /// </summary>
        public MARC.HI.EHRS.SVC.Core.HostContext Context
        {
            get { return m_context; }
            set
            {
                m_context = value;

                if (value == null) return;
                this.m_auditorService = value.GetService(typeof(IAuditorService)) as IAuditorService;
                this.m_configService = value.GetService(typeof(ISystemConfigurationService)) as ISystemConfigurationService;
                this.m_persistenceService = value.GetService(typeof(IDataPersistenceService)) as IDataPersistenceService;
                this.m_decisionService = value.GetService(typeof(IDecisionSupportService)) as IDecisionSupportService;
                this.m_docRegService = value.GetService(typeof(IDataRegistrationService)) as IDataRegistrationService;
                this.m_policyService = value.GetService(typeof(IPolicyEnforcementService)) as IPolicyEnforcementService;
                this.m_queryService = value.GetService(typeof(IQueryPersistenceService)) as IQueryPersistenceService;
                this.m_localeService = value.GetService(typeof(ILocalizationService)) as ILocalizationService;
            }
        }

        #endregion

        /// <summary>
        /// Register health service record with the data persistence engine
        /// </summary>
        internal VersionedDomainIdentifier Register(RegistrationEvent healthServiceRecord, List<MARC.Everest.Connectors.IResultDetail> dtls, List<DetectedIssue> issues, DataPersistenceMode mode)
        {

            // persistence services

            try
            {
                // Can't find persistence
                if (this.m_persistenceService == null)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Couldn't locate an implementation of a PersistenceService object, storage is aborted", null));
                    return null;
                }
                else if (healthServiceRecord == null)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Can't register null health service record data", null));
                    return null;
                }
                else if (dtls.Count(o => o.Type == ResultDetailType.Error) > 0)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Won't attempt to persist invalid message", null));
                    return null;
                }

                // Call the dss
                if (this.m_decisionService != null)
                    issues.AddRange(this.m_decisionService.RecordPersisting(healthServiceRecord));

                // Any errors?
                if (issues.Count(o => o.Priority == IssuePriorityType.Error) > 0)
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Won't attempt to persist message due to detected issues", null));

                // Return value
                var retVal = this.m_persistenceService.StoreContainer(healthServiceRecord, mode);

                // Audit the creation
                if (this.m_auditorService != null)
                {
                    AuditData auditData = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.Success, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, auditData, 0);
                    UpdateAuditData(AuditableObjectLifecycle.Creation, new List<VersionedDomainIdentifier>(new VersionedDomainIdentifier[] { retVal }), auditData);
                    this.m_auditorService.SendAudit(auditData);
                }

                // Call the dss
                if (this.m_decisionService != null)
                    this.m_decisionService.RecordPersisted(healthServiceRecord);

                // Register the document set if it is a document
                if (retVal != null && this.m_docRegService != null && !this.m_docRegService.RegisterRecord(healthServiceRecord, mode))
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Warning, "Wasn't able to register event in the event registry, event exists in repository but not in registry. You may not be able to query for this event", null));

                return retVal;
            }
            catch (DuplicateNameException ex) // Already persisted stuff
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.AlreadyPerformed,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (MissingPrimaryKeyException ex) // Already persisted stuff
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.DetectedIssue,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (ConstraintException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.DetectedIssue,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (DbException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
            catch (DataException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
            catch (IssueException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                issues.Add(ex.Issue);
                return null;
            }
            catch (Exception ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new ResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }

        }

        /// <summary>
        /// Get record
        /// </summary>
        internal RegistrationEvent GetRecord(VersionedDomainIdentifier recordId, List<IResultDetail> dtls, List<DetectedIssue> issues, QueryData qd)
        {
            try
            {
                // Can't find persistence
                if (this.m_persistenceService == null)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Couldn't locate an implementation of a PersistenceService object, storage is aborted", null));
                    throw new Exception("Cannot de-persist records");
                }


                // Read the record from the DB
                var result = this.m_persistenceService.GetContainer(recordId, qd.IsSummary) as RegistrationEvent;

                // Does this result match what we're looking for?
                if (result == null)
                    return null; // next record

                // Are we interested in any of the history?
                if (!qd.IncludeHistory)
                    result.RemoveAllFromRole(HealthServiceRecordSiteRoleType.OlderVersionOf);
                if (!qd.IncludeNotes)
                {
                    var notes = result.FindAllComponents(HealthServiceRecordSiteRoleType.CommentOn);
                    foreach (var n in notes ?? new List<IComponent>())
                        (n as Annotation).IsMasked = true;
                }

                // Mask
                if (this.m_policyService != null)
                    result = this.m_policyService.ApplyPolicies(qd.QueryRequest, result, issues) as RegistrationEvent;

                return result;
            }
            catch (Exception ex)
            {
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
        }

        /// <summary>
        /// Get components from the persistence service
        /// </summary>
        /// <remarks>
        /// Calls are as follows:
        /// <list type="bullet">
        ///     <item></item>
        /// </list>
        /// </remarks>
        internal QueryResultData Get(VersionedDomainIdentifier[] recordIds, List<IResultDetail> dtls, List<DetectedIssue> issues, QueryData qd)
        {

            try
            {

                List<VersionedDomainIdentifier> retRecordId = new List<VersionedDomainIdentifier>(100);
                // Query continuation
                if (this.m_queryService != null && this.m_queryService.IsRegistered(qd.QueryId))
                {
                    throw new Exception(String.Format("The query '{0}' has already been registered. To continue this query use the QUQI_IN000003CA interaction", qd.QueryId));
                }
                else
                {

                    var retVal = GetRecordsAsync(recordIds, retRecordId, issues, dtls, qd);

                    // Get the count of not-included records
                    retVal.RemoveAll(o => o == null);

                    // Persist the query
                    if (this.m_queryService != null)
                        this.m_queryService.RegisterQuerySet(qd.QueryId, recordIds, qd);

                    // Return query data
                    return new QueryResultData()
                    {
                        QueryId = qd.QueryId,
                        Results = retVal.ToArray(),
                        TotalResults = retRecordId.Count
                    };

                }

            }
            catch (TimeoutException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(qd.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (DbException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(qd.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (DataException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(qd.QueryRequest, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (Exception ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(qd.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new ResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
        }

        /// <summary>
        /// Get all records asynchronously
        /// </summary>
        /// <param name="recordIds">Record identifiers to retrieve</param>
        /// <param name="retRecordId">An array of record identiifers actually returned</param>
        internal List<RegistrationEvent> GetRecordsAsync(VersionedDomainIdentifier[] recordIds, List<VersionedDomainIdentifier> retRecordId, List<DetectedIssue> issues, List<IResultDetail> dtls, QueryData qd)
        {
            // Decision Support service
            RegistrationEvent[] retVal = new RegistrationEvent[qd.Quantity < recordIds.Length ? qd.Quantity : recordIds.Length];
            retRecordId.AddRange(recordIds);

            List<VersionedDomainIdentifier> recordFetch = new List<VersionedDomainIdentifier>(retVal.Length);
            // Get the number of records to include
            for(int i = 0; i < retVal.Length; i++)
                recordFetch.Add(recordIds[i]);

            int maxWorkerBees = Environment.ProcessorCount * 4,
                nResults = 0;
            //List<Thread> workerBees = new List<Thread>(maxWorkerBees);  // Worker bees
            var wtp = new MARC.Everest.Threading.WaitThreadPool(maxWorkerBees);
            try
            {

                //// Get components
                foreach (var id in recordFetch)
                    wtp.QueueUserWorkItem((WaitCallback)delegate(object parm)
                            {
                                List<IResultDetail> mDtls = new List<IResultDetail>(10);
                                List<DetectedIssue> mIssue = new List<DetectedIssue>(10);

                                // DSS Service
                                if (this.m_decisionService != null)
                                    mIssue.AddRange(this.m_decisionService.RetrievingRecord(id));

                                var result = GetRecord(parm as VersionedDomainIdentifier, mDtls, mIssue, qd);

                                // DSS Service
                                if (this.m_decisionService != null)
                                    mIssue.AddRange(this.m_decisionService.RetrievedRecord(result));

                                // Process result
                                if (result != null)
                                {
                                    // Container has been retrieved
                                    if (this.m_decisionService != null)
                                        mIssue.AddRange(this.m_decisionService.RetrievedRecord(result));

                                    // Add to the results
                                    lock (syncLock)
                                    {
                                        // Add return value
                                        if (retRecordId.IndexOf(parm as VersionedDomainIdentifier) < retVal.Length)
                                            retVal[retRecordId.IndexOf(parm as VersionedDomainIdentifier)] = result;

                                    }
                                }
                                else
                                {
                                    mIssue.Add(new DetectedIssue()
                                    {
                                        Type = IssueType.BusinessConstraintViolation,
                                        Text = String.Format("Record '{0}@{1}' will not be retrieved", id.Domain, (parm as VersionedDomainIdentifier).Identifier),
                                        MitigatedBy = ManagementType.OtherActionTaken,
                                        Priority = IssuePriorityType.Warning
                                    });
                                }

                                // Are we disclosing this record?
                                if (result == null || result.IsMasked)
                                    lock (syncLock)
                                        retRecordId.Remove(parm as VersionedDomainIdentifier);

                                // Add issues and details
                                lock (syncLock)
                                {
                                    issues.AddRange(mIssue);
                                    dtls.AddRange(mDtls);
                                }
                            }, id
                        );
                // for
                bool didReturn = wtp.WaitOne(20000, true);

                if (!didReturn)
                    throw new TimeoutException("The query could not complete in the specified amount of time");

                // Audit the event
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, retRecordId.Count == 0 ? OutcomeIndicator.MinorFail : OutcomeIndicator.Success, EventIdentifierType.Query, null);
                    UpdateAuditData(qd.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    UpdateAuditData(AuditableObjectLifecycle.Disclosure, retRecordId, audit);
                    this.m_auditorService.SendAudit(audit);
                }
            }
            finally
            {
                wtp.Dispose();
            }

            return new List<RegistrationEvent>(retVal);
        }

        /// <summary>
        /// Update audit data for disclosure purposes
        /// </summary>
        internal void UpdateAuditData(AuditableObjectLifecycle lifeCycle, List<VersionedDomainIdentifier> retRecordId, AuditData audit)
        {
            foreach (var id in retRecordId)
            {
                audit.AuditableObjects.Add(new AuditableObject()
                {
                    LifecycleType = lifeCycle,
                    IDTypeCode = AuditableObjectIdType.ReportNumber,
                    ObjectId = String.Format("{0}@{1}", id.Domain, id.Identifier),
                    Role = AuditableObjectRole.Report,
                    Type = AuditableObjectType.SystemObject
                });
            }
        }

        
        /// <summary>
        /// Update auditing data
        /// </summary>
        private void UpdateAuditData(RegistrationEvent queryRequest, AuditData audit, AuditableObjectLifecycle? lifeCycle)
        {

            // Add an actor for the current server
            audit.Actors.Add(new AuditActorData()
            {
                ActorRoleCode = new List<string>() { "RCV" },
                NetworkAccessPointId = Environment.MachineName,
                UserIsRequestor = false,
                NetworkAccessPointType = NetworkAccessPointType.MachineName
            });

            // Look for policy override information
            var policyOverride = queryRequest.FindComponent(HealthServiceRecordSiteRoleType.ConsentOverrideFor) as PolicyOverride;
            if (policyOverride != null)
                audit.AuditableObjects.Add(new AuditableObject()
                {
                    IDTypeCode = AuditableObjectIdType.ReportNumber,
                    LifecycleType = AuditableObjectLifecycle.Verification,
                    Role = AuditableObjectRole.SecurityResource,
                    Type = AuditableObjectType.SystemObject,
                    ObjectId = String.Format("{0}@{1}", policyOverride.FormId.Domain, policyOverride.FormId.Identifier)
                });

            // Add a network node
            foreach (IComponent comp in queryRequest.Components)
            {
                // Healthcare participant = actor
                if (comp is HealthcareParticipant)
                {
                    audit.Actors.Add(new AuditActorData()
                    {
                        ActorRoleCode = new List<string>(new string[] { comp.Site.Name }),
                        UserIdentifier = String.Format("{0}@{1}", this.m_configService.OidRegistrar.GetOid("CR_PID").Oid, (comp as HealthcareParticipant).Id.ToString()),
                        UserName = (comp as HealthcareParticipant).LegalName.ToString(),
                        UserIsRequestor = (comp.Site as HealthServiceRecordSite).SiteRoleType == HealthServiceRecordSiteRoleType.AuthorOf,

                    });
                    audit.AuditableObjects.Add(new AuditableObject()
                    {
                        IDTypeCode = AuditableObjectIdType.UserIdentifier,
                        ObjectId = String.Format("{0}@{1}", this.m_configService.OidRegistrar.GetOid("CR_PID").Oid, (comp as HealthcareParticipant).Id.ToString()),
                        Role = AuditableObjectRole.Provider,
                        Type = AuditableObjectType.Person,
                        LifecycleType = (comp.Site as HealthServiceRecordSite).SiteRoleType == HealthServiceRecordSiteRoleType.AuthorOf ? lifeCycle.Value : default(AuditableObjectLifecycle)
                    });
                }

            }
        }

        /// <summary>
        /// Query (list) the data from the persistence layer
        /// </summary>
        internal QueryResultData Query(QueryData filter, List<IResultDetail> dtls, List<DetectedIssue> issues)
        {

            try
            {

                List<VersionedDomainIdentifier> retRecordId = new List<VersionedDomainIdentifier>(100);
                // Query continuation
                if (this.m_docRegService == null)
                    throw new InvalidOperationException("No record registration service is registered. Querying for records cannot be done unless this service is present");
                else if (this.m_queryService != null && this.m_queryService.IsRegistered(filter.QueryId))
                    throw new Exception(String.Format("The query '{0}' has already been registered. To continue this query use the QUQI_IN000003CA interaction", filter.QueryId));
                else
                {

                    // Query the document registry service
                    var queryFilter = filter.QueryRequest.FindComponent(HealthServiceRecordSiteRoleType.FilterOf); // The outer filter data is usually just parameter control..

                    var recordIds = this.m_docRegService.QueryRecord(queryFilter as HealthServiceRecordComponent);
                    var retVal = GetRecordsAsync(recordIds, retRecordId, issues, dtls, filter);

                    // Sort control?
                    // TODO: Support sort control
                    //retVal.Sort((a, b) => b.Id.CompareTo(a.Id)); // Default sort by id

                    // Persist the query
                    if (this.m_queryService != null)
                        this.m_queryService.RegisterQuerySet(filter.QueryId, recordIds, filter);

                    // Return query data
                    return new QueryResultData()
                    {
                        QueryId = filter.QueryId,
                        Results = retVal.ToArray(),
                        TotalResults = recordIds.Length
                    };

                }

            }
            catch (TimeoutException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(filter.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (DbException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(filter.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (DataException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(filter.QueryRequest, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
            catch (Exception ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Read, OutcomeIndicator.EpicFail, EventIdentifierType.Query, null);
                    UpdateAuditData(filter.QueryRequest, audit, AuditableObjectLifecycle.ReceiptOfDisclosure);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new ResultDetail(ResultDetailType.Error, ex.Message, ex));
                return QueryResultData.Empty;
            }
        }

        /// <summary>
        /// Update a health service record component
        /// </summary>
        internal VersionedDomainIdentifier Update(RegistrationEvent healthServiceRecord, List<IResultDetail> dtls, List<DetectedIssue> issues, DataPersistenceMode mode)
        {
            // persistence services

            try
            {
                // Can't find persistence
                if (this.m_persistenceService == null)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Couldn't locate an implementation of a PersistenceService object, storage is aborted", null));
                    return null;
                }
                else if (healthServiceRecord == null)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Can't register null health service record data", null));
                    return null;
                }
                else if (dtls.Count(o => o.Type == ResultDetailType.Error) > 0)
                {
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Won't attempt to persist invalid message", null));
                    return null;
                }

                // Call the dss
                if (this.m_decisionService != null)
                    issues.AddRange(this.m_decisionService.RecordPersisting(healthServiceRecord));

                // Any errors?
                if (issues.Count(o => o.Priority == IssuePriorityType.Error) > 0)
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, "Won't attempt to persist message due to detected issues", null));

                // Return value
                var retVal = this.m_persistenceService.UpdateContainer(healthServiceRecord, mode);

                // Audit the creation
                if (this.m_auditorService != null)
                {
                    AuditData auditData = new AuditData(DateTime.Now, ActionType.Update, OutcomeIndicator.Success, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, auditData, 0);
                    UpdateAuditData(AuditableObjectLifecycle.Amendment, new List<VersionedDomainIdentifier>(new VersionedDomainIdentifier[] { retVal }), auditData);
                    this.m_auditorService.SendAudit(auditData);
                }

                // Call the dss
                if (this.m_decisionService != null)
                    this.m_decisionService.RecordPersisted(healthServiceRecord);

                // Register the document set if it is a document
                if (retVal != null && this.m_docRegService != null && !this.m_docRegService.RegisterRecord(healthServiceRecord, mode))
                    dtls.Add(new PersistenceResultDetail(ResultDetailType.Warning, "Wasn't able to register event in the event registry, event exists in repository but not in registry. You may not be able to query for this event", null));

                return retVal;
            }
            catch (DuplicateNameException ex) // Already persisted stuff
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.AlreadyPerformed,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (MissingPrimaryKeyException ex) // Already persisted stuff
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.DetectedIssue,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (ConstraintException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Create, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }
                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, m_localeService.GetString("DTPE005"), ex));
                issues.Add(new DetectedIssue()
                {
                    Severity = IssueSeverityType.High,
                    Type = IssueType.DetectedIssue,
                    Text = ex.Message,
                    Priority = IssuePriorityType.Error
                });
                return null;
            }
            catch (DbException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Update, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
            catch (DataException ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Update, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new PersistenceResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
            catch (Exception ex)
            {
                // Audit exception
                if (this.m_auditorService != null)
                {
                    AuditData audit = new AuditData(DateTime.Now, ActionType.Update, OutcomeIndicator.EpicFail, EventIdentifierType.ProvisioningEvent, null);
                    UpdateAuditData(healthServiceRecord, audit, 0);
                    this.m_auditorService.SendAudit(audit);
                }

                dtls.Add(new ResultDetail(ResultDetailType.Error, ex.Message, ex));
                return null;
            }
        }
    }
}