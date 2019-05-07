// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using NuGet.Server.Core;
using NuGet.Server.Core.DataServices;
using NuGet.Server.Core.Infrastructure;
using NuGet.Server.V2.Model;
using NuGet.Server.V2.OData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.OData;
using System.Web.Http.OData.Query;

namespace NuGet.Server.V2.Controllers {
    [NuGetODataControllerConfiguration]
    public abstract class NuGetODataController : ODataController {
        private const string ApiKeyHeader = "X-NUGET-APIKEY";

        protected int _maxPageSize = 25;

        protected readonly IServerPackageRepository _serverRepository;
        protected readonly IPackageAuthenticationService _authenticationService;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="repository">Required.</param>
        /// <param name="authenticationService">Optional. If this is not supplied Upload/Delete is not available (requests returns 403 Forbidden)</param>
        protected NuGetODataController(
            IServerPackageRepository repository,
            IPackageAuthenticationService authenticationService = null) {
            this._serverRepository = repository ?? throw new ArgumentNullException(nameof(repository));
            this._authenticationService = authenticationService;
        }

        // GET /Packages
        [HttpGet]
        public virtual async Task<IHttpActionResult> Get(
            ODataQueryOptions<ODataPackage> options,
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            ClientCompatibility clientCompatibility = ClientCompatibilityFactory.FromProperties(semVerLevel);

            IEnumerable<IServerPackage> sourceQuery = await this._serverRepository.GetPackagesAsync(clientCompatibility, token);

            return this.TransformToQueryResult(options, sourceQuery, clientCompatibility);
        }

        // GET /Packages/$count
        [HttpGet]
        public virtual async Task<IHttpActionResult> GetCount(
            ODataQueryOptions<ODataPackage> options,
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) => (await this.Get(options, semVerLevel, token)).FormattedAsCountResult<ODataPackage>();

        // GET /Packages(Id=,Version=)
        [HttpGet]
        public virtual async Task<IHttpActionResult> Get(
            ODataQueryOptions<ODataPackage> options,
            string id,
            string version,
            CancellationToken token) {
            IServerPackage package = await this.RetrieveFromRepositoryAsync(id, version, token);

            if (package == null) {
                return this.NotFound();
            }

            return this.TransformToQueryResult(options, new[] { package }, ClientCompatibility.Max)
                .FormattedAsSingleResult<ODataPackage>();
        }

        // GET/POST /FindPackagesById()?id=
        [HttpGet]
        [HttpPost]
        public virtual async Task<IHttpActionResult> FindPackagesById(
            ODataQueryOptions<ODataPackage> options,
            [FromODataUri] string id,
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            if (string.IsNullOrEmpty(id)) {
                IQueryable<ODataPackage> emptyResult = Enumerable.Empty<ODataPackage>().AsQueryable();
                return this.QueryResult(options, emptyResult, this._maxPageSize);
            }

            ClientCompatibility clientCompatibility = ClientCompatibilityFactory.FromProperties(semVerLevel);

            IEnumerable<IServerPackage> sourceQuery = await this._serverRepository.FindPackagesByIdAsync(id, clientCompatibility, token);

            return this.TransformToQueryResult(options, sourceQuery, clientCompatibility);
        }


        // GET /Packages(Id=,Version=)/propertyName
        [HttpGet]
        public virtual IHttpActionResult GetPropertyFromPackages(string propertyName, string id, string version) {
            switch (propertyName.ToLowerInvariant()) {
                case "id":
                    return this.Ok(id);
                case "version":
                    return this.Ok(version);
            }

            return this.BadRequest("Querying property " + propertyName + " is not supported.");
        }

        // GET/POST /Search()?searchTerm=&targetFramework=&includePrerelease=
        [HttpGet]
        [HttpPost]
        public virtual async Task<IHttpActionResult> Search(
            ODataQueryOptions<ODataPackage> options,
            [FromODataUri] string searchTerm = "",
            [FromODataUri] string targetFramework = "",
            [FromODataUri] bool includePrerelease = false,
            [FromODataUri] bool includeDelisted = false,
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            IEnumerable<string> targetFrameworks = string.IsNullOrEmpty(targetFramework) ? Enumerable.Empty<string>() : targetFramework.Split('|');

            ClientCompatibility clientCompatibility = ClientCompatibilityFactory.FromProperties(semVerLevel);

            IEnumerable<IServerPackage> sourceQuery = await this._serverRepository.SearchAsync(
                searchTerm,
                targetFrameworks,
                includePrerelease,
                includeDelisted,
                clientCompatibility,
                token);

            return this.TransformToQueryResult(options, sourceQuery, clientCompatibility);
        }

        // GET /Search()/$count?searchTerm=&targetFramework=&includePrerelease=
        [HttpGet]
        public virtual async Task<IHttpActionResult> SearchCount(
            ODataQueryOptions<ODataPackage> options,
            [FromODataUri] string searchTerm = "",
            [FromODataUri] string targetFramework = "",
            [FromODataUri] bool includePrerelease = false,
            [FromODataUri] bool includeDelisted = false,
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            IHttpActionResult searchResults = await this.Search(
                options,
                searchTerm,
                targetFramework,
                includePrerelease,
                includeDelisted,
                semVerLevel,
                token);

            return searchResults.FormattedAsCountResult<ODataPackage>();
        }

        // GET/POST /GetUpdates()?packageIds=&versions=&includePrerelease=&includeAllVersions=&targetFrameworks=&versionConstraints=
        [HttpGet]
        [HttpPost]
        public virtual async Task<IHttpActionResult> GetUpdates(
            ODataQueryOptions<ODataPackage> options,
            [FromODataUri] string packageIds,
            [FromODataUri] string versions,
            [FromODataUri] bool includePrerelease,
            [FromODataUri] bool includeAllVersions,
            [FromODataUri] string targetFrameworks = "",
            [FromODataUri] string versionConstraints = "",
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            if (string.IsNullOrEmpty(packageIds) || string.IsNullOrEmpty(versions)) {
                return this.Ok(Enumerable.Empty<ODataPackage>().AsQueryable());
            }

            // Workaround https://github.com/NuGet/NuGetGallery/issues/674 for NuGet 2.1 client.
            // Can probably eventually be retired (when nobody uses 2.1 anymore...)
            // Note - it was URI un-escaping converting + to ' ', undoing that is actually a pretty conservative substitution because
            // space characters are never acepted as valid by VersionUtility.ParseFrameworkName.
            if (!string.IsNullOrEmpty(targetFrameworks)) {
                targetFrameworks = targetFrameworks.Replace(' ', '+');
            }

            string[] idValues = packageIds.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            string[] versionValues = versions.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            List<System.Runtime.Versioning.FrameworkName> targetFrameworkValues = string.IsNullOrEmpty(targetFrameworks)
                                        ? null
                                        : targetFrameworks.Split('|').Select(VersionUtility.ParseFrameworkName).ToList();
            List<string> versionConstraintValues = (string.IsNullOrEmpty(versionConstraints)
                                            ? new string[idValues.Length]
                                            : versionConstraints.Split('|')).ToList();

            if (idValues.Length == 0 || idValues.Length != versionValues.Length || idValues.Length != versionConstraintValues.Count) {
                // Exit early if the request looks invalid
                return this.Ok(Enumerable.Empty<ODataPackage>().AsQueryable());
            }

            List<IPackageMetadata> packagesToUpdate = new List<IPackageMetadata>();
            for (int i = 0; i < idValues.Length; i++) {
                if (SemanticVersion.TryParse(versionValues[i], out SemanticVersion semVersion)) {
                    packagesToUpdate.Add(new PackageBuilder { Id = idValues[i], Version = semVersion });
                } else {
                    versionConstraintValues.RemoveAt(i);
                }

            }

            IVersionSpec[] versionConstraintsList = new IVersionSpec[versionConstraintValues.Count];
            for (int i = 0; i < versionConstraintsList.Length; i++) {
                if (!string.IsNullOrEmpty(versionConstraintValues[i])) {
                    VersionUtility.TryParseVersionSpec(versionConstraintValues[i], out versionConstraintsList[i]);
                }
            }

            ClientCompatibility clientCompatibility = ClientCompatibilityFactory.FromProperties(semVerLevel);

            IEnumerable<IServerPackage> sourceQuery = await this._serverRepository.GetUpdatesAsync(
                packagesToUpdate,
                includePrerelease,
                includeAllVersions,
                targetFrameworkValues,
                versionConstraintsList,
                clientCompatibility,
                token);

            return this.TransformToQueryResult(options, sourceQuery, clientCompatibility);
        }

        // /api/v2/GetUpdates()/$count?packageIds=&versions=&includePrerelease=&includeAllVersions=&targetFrameworks=&versionConstraints=
        [HttpGet]
        [HttpPost]
        public virtual async Task<IHttpActionResult> GetUpdatesCount(
            ODataQueryOptions<ODataPackage> options,
            [FromODataUri] string packageIds,
            [FromODataUri] string versions,
            [FromODataUri] bool includePrerelease,
            [FromODataUri] bool includeAllVersions,
            [FromODataUri] string targetFrameworks = "",
            [FromODataUri] string versionConstraints = "",
            [FromUri] string semVerLevel = "",
            CancellationToken token = default(CancellationToken)) {
            IHttpActionResult updates = await this.GetUpdates(
                options,
                packageIds,
                versions,
                includePrerelease,
                includeAllVersions,
                targetFrameworks,
                versionConstraints,
                semVerLevel,
                token);

            return updates.FormattedAsCountResult<ODataPackage>();
        }

        /// <summary>
        /// Exposed as OData Action for specific entity
        /// GET/HEAD /Packages(Id=,Version=)/Download
        /// </summary>
        /// <param name="id"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        [HttpGet, HttpHead]
        public virtual async Task<HttpResponseMessage> Download(
            string id,
            string version = "",
            CancellationToken token = default(CancellationToken)) {
            IServerPackage requestedPackage = await this.RetrieveFromRepositoryAsync(id, version, token);

            if (requestedPackage == null) {
                return this.Request.CreateErrorResponse(HttpStatusCode.NotFound, string.Format("'Package {0} {1}' Not found.", id, version));
            }

            HttpResponseMessage responseMessage = this.Request.CreateResponse(HttpStatusCode.OK);

            if (this.Request.Method == HttpMethod.Get) {
                responseMessage.Content = new StreamContent(File.OpenRead(requestedPackage.FullPath));
            } else {
                responseMessage.Content = new StringContent(string.Empty);
            }

            responseMessage.Content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("binary/octet-stream");
            if (requestedPackage != null) {
                responseMessage.Content.Headers.LastModified = requestedPackage.LastUpdated;
                responseMessage.Headers.ETag = new EntityTagHeaderValue('"' + requestedPackage.PackageHash + '"');
            }

            responseMessage.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment) {
                FileName = string.Format("{0}.{1}{2}", requestedPackage.Id, requestedPackage.Version, NuGet.Constants.PackageExtension),
                Size = requestedPackage != null ? (long?)requestedPackage.PackageSize : null,
                ModificationDate = responseMessage.Content.Headers.LastModified,
            };

            return responseMessage;
        }

        /// <summary>
        /// Exposed through ordinary Web API route. Bypasses OData pipeline.
        /// DELETE /id/version
        /// </summary>
        /// <param name="id"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        [HttpDelete]
        public virtual async Task<HttpResponseMessage> DeletePackage(
            string id,
            string version,
            CancellationToken token) {
            if (this._authenticationService == null) {
                return this.Request.CreateErrorResponse(HttpStatusCode.Forbidden, "Package delete is not allowed");
            }

            string apiKey = this.GetApiKeyFromHeader();

            IServerPackage requestedPackage = await this.RetrieveFromRepositoryAsync(id, version, token);

            if (requestedPackage == null || !requestedPackage.Listed) {
                // Package not found
                return this.CreateStringResponse(HttpStatusCode.NotFound, string.Format("'Package {0} {1}' Not found.", id, version)); // Request.CreateErrorResponse(HttpStatusCode.NotFound, string.Format("'Package {0} {1}' Not found.", id, version));
            }

            // Make sure the user can access this package
            if (this._authenticationService.IsAuthenticated(this.User, apiKey, requestedPackage.Id)) {
                await this._serverRepository.RemovePackageAsync(requestedPackage.Id, requestedPackage.Version, token);
                return this.Request.CreateResponse(HttpStatusCode.NoContent);
            } else {
                return this.CreateStringResponse(HttpStatusCode.Forbidden, string.Format("Access denied for package '{0}', version '{1}'.", requestedPackage.Id, version));
            }
        }

        /// <summary>
        /// Exposed through ordinary Web API route. Bypasses OData pipeline.
        /// PUT /
        /// </summary>
        /// <returns></returns>
        [HttpPut]
        public virtual async Task<HttpResponseMessage> UploadPackage(CancellationToken token) {
            if (this._authenticationService == null) {
                return this.Request.CreateErrorResponse(HttpStatusCode.Forbidden, "Package upload is not allowed");
            }

            string apiKey = this.GetApiKeyFromHeader();

            // Copy the package to a temporary file
            string temporaryFile = Path.GetTempFileName();
            using (FileStream temporaryFileStream = File.Open(temporaryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
                if (this.Request.Content.IsMimeMultipartContent()) {
                    MultipartMemoryStreamProvider multipartContents = await this.Request.Content.ReadAsMultipartAsync();
                    await multipartContents.Contents.First().CopyToAsync(temporaryFileStream);
                } else {
                    await this.Request.Content.CopyToAsync(temporaryFileStream);
                }
            }

            IPackage package = PackageFactory.Open(temporaryFile);

            HttpResponseMessage retValue;
            if (this._authenticationService.IsAuthenticated(this.User, apiKey, package.Id)) {

                /**** Remove That matched same version but not same build number *****/
                SemanticVersion uploadVersion = package.Version;
                IEnumerable<IServerPackage> previousPackages = await this._serverRepository.GetPackagesAsync(ClientCompatibility.Max, token);
                IEnumerable<IServerPackage> matchedPackages = previousPackages.Where(p => p.Id == package.Id && p.Version.Version.ToString(3) == uploadVersion.Version.ToString(3));
                foreach (IServerPackage p in matchedPackages) {
                    await this._serverRepository.RemovePackageAsync(p.Id, p.Version, token);
                }
                /***** *****/

                await this._serverRepository.AddPackageAsync(package, token);
                retValue = this.Request.CreateResponse(HttpStatusCode.Created);
            } else {
                retValue = this.CreateStringResponse(HttpStatusCode.Forbidden, string.Format("Access denied for package '{0}'.", package.Id));
            }

            package = null;
            try {
                File.Delete(temporaryFile);
            } catch (Exception) {
                retValue = this.CreateStringResponse(HttpStatusCode.InternalServerError, "Could not remove temporary upload file.");
            }

            return retValue;
        }

        protected HttpResponseMessage CreateStringResponse(HttpStatusCode statusCode, string response) {
            HttpResponseMessage responseMessage = new HttpResponseMessage(statusCode) { Content = new StringContent(response) };
            return responseMessage;
        }

        private string GetApiKeyFromHeader() {
            string apiKey = null;
            if (this.Request.Headers.TryGetValues(ApiKeyHeader, out IEnumerable<string> values)) {
                apiKey = values.FirstOrDefault();
            }

            return apiKey;
        }

        protected async Task<IServerPackage> RetrieveFromRepositoryAsync(
            string id,
            string version,
            CancellationToken token) {
            if (string.IsNullOrEmpty(version)) {
                return await this._serverRepository.FindPackageAsync(id, ClientCompatibility.Max, token);
            }

            return await this._serverRepository.FindPackageAsync(id, new SemanticVersion(version), token);
        }

        protected IQueryable<ODataPackage> TransformPackages(
            IEnumerable<IServerPackage> packages,
            ClientCompatibility compatibility) {
            return packages
                .Distinct()
                .Select(x => x.AsODataPackage(compatibility))
                .AsQueryable()
                .InterceptWith(new NormalizeVersionInterceptor());
        }

        /// <summary>
        /// Generates a QueryResult.
        /// </summary>
        /// <typeparam name="TModel">Model type.</typeparam>
        /// <param name="options">OData query options.</param>
        /// <param name="queryable">Queryable to build QueryResult from.</param>
        /// <param name="maxPageSize">Maximum page size.</param>
        /// <returns>A QueryResult instance.</returns>
        protected virtual IHttpActionResult QueryResult<TModel>(ODataQueryOptions<TModel> options, IQueryable<TModel> queryable, int maxPageSize) => new QueryResult<TModel>(options, queryable, this, maxPageSize);

        /// <summary>
        /// Transforms IPackages to ODataPackages and generates a QueryResult<ODataPackage></ODataPackage>
        /// </summary>
        /// <param name="options"></param>
        /// <param name="sourceQuery"></param>
        /// <returns></returns>
        protected virtual IHttpActionResult TransformToQueryResult(
            ODataQueryOptions<ODataPackage> options,
            IEnumerable<IServerPackage> sourceQuery,
            ClientCompatibility compatibility) {
            IQueryable<ODataPackage> transformedQuery = this.TransformPackages(sourceQuery, compatibility);
            return this.QueryResult(options, transformedQuery, this._maxPageSize);
        }
    }
}
