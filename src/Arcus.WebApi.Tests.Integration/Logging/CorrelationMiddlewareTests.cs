﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Arcus.Observability.Correlation;
using Arcus.Testing.Logging;
using Arcus.WebApi.Logging.Core.Correlation;
using Arcus.WebApi.Tests.Integration.Fixture;
using Arcus.WebApi.Tests.Integration.Logging.Controllers;
using Arcus.WebApi.Tests.Integration.Logging.Fixture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Arcus.WebApi.Tests.Integration.Logging
{
    [Collection("Integration")]
    public class CorrelationMiddlewareTests
    {
        private const string DefaultOperationId = "X-Operation-ID", 
                             DefaultTransactionId = "X-Transaction-ID",
                             DefaultOperationParentId = "Request-Id";

        private readonly ILogger _logger;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="CorrelationMiddlewareTests" /> class.
        /// </summary>
        public CorrelationMiddlewareTests(ITestOutputHelper outputWriter)
        {
            _logger = new XunitTestLogger(outputWriter);
        }
        
        [Fact]
        public async Task SendRequest_WithCorrelateOptionsNotAllowTransactionInRequest_ResponseWithBadRequest()
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Transaction.AllowInRequest = false))
                .Configure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultTransactionId, $"transaction-{Guid.NewGuid()}");
                
                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultOperationParentId);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultTransactionId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelationInfoOptionsNotGenerateTransactionId_ResponseWithoutTransactionId()
        {
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Transaction.GenerateWhenNotSpecified = false))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultTransactionId);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Null(correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelateOptionsNonTransactionIncludeInResponse_ResponseWithoutCorrelationHeaders()
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Transaction.IncludeInResponse = false))
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var service = await TestApiServer.StartNewAsync(options, _logger))
            {
                // Act
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);
                using (HttpResponseMessage response = await service.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultOperationParentId);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultTransactionId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.NotEmpty(correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelateOptionsCustomGenerateTransactionId_ResponseWitCustomGeneratedTransactionId()
        {
            // Arrange
            var expectedTransactionId = $"transaction-{Guid.NewGuid():N}";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Transaction.GenerateId = () => expectedTransactionId))
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);
                
                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string actualTransactionId = GetResponseHeader(response, DefaultTransactionId);
                    Assert.Equal(expectedTransactionId, actualTransactionId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(expectedTransactionId, correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelateOptionsNonOperationIncludeInResponse_ResponseWithoutCorrelationHeaders()
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Operation.IncludeInResponse = true))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);
                
                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithoutCorrelationHeaders_ResponseWithCorrelationHeadersAndCorrelationAccess()
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(options => options.UpstreamService.ExtractFromRequest = false))
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);
                
                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string parentId = GetResponseHeader(response, DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(parentId, correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelationHeader_ResponseWithSameCorrelationHeader()
        {
            // Arrange
            string expected = $"transaction-{Guid.NewGuid()}";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation())
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultTransactionId, expected);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    
                    string actual = GetResponseHeader(response, DefaultTransactionId);
                    Assert.Equal(expected, actual);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(expected, correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithRequestIdHeader_ResponseWithDifferentRequestIdHeader()
        {
            // Arrange
            string expected = $"parent{Guid.NewGuid()}".Replace("-", "");
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation())
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultOperationParentId, expected);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string parentId = GetResponseHeader(response, DefaultOperationParentId);
                    Assert.Equal(expected, parentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(expected, correlation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequest_WithCorrelateOptionsCustomGenerateOperationId_ResponseWitCustomGeneratedOperationId()
        {
            // Arrange
            var expectedOperationId = $"operation-{Guid.NewGuid():N}";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.Operation.GenerateId = () => expectedOperationId))
                .PreConfigure(app => app.UseTraceIdentifier(opt => opt.EnableTraceIdentifier = false)
                                        .UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(CorrelationController.GetRoute);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultOperationParentId);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.Equal(expectedOperationId, correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Null(correlation.OperationParentId);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendRequest_WithCorrelateOptionsUpstreamServiceExtractFromRequest_UsesUpstreamOperationId(bool extractFromRequest)
        {
            // Arrange
            var operationParentId = Guid.NewGuid().ToString();
            var requestId = $"|{Guid.NewGuid()}.{operationParentId}";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.UpstreamService.ExtractFromRequest = extractFromRequest))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultOperationParentId, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.NotEmpty(correlation.TransactionId);
                    Assert.Equal(extractFromRequest, operationParentId == correlation.OperationParentId);
                }
            }
        }

        [Theory]
        [InlineData(false, "|abc.def", null)]
        [InlineData(true, "|abc.def", "def")]
        [InlineData(false, "abc", null)]
        [InlineData(true, "abc", "abc")]
        public async Task SendRequest_WithCorrelateOptionsUpstreamServiceExtractFromRequest_UsesRequestId(bool extractFromRequest, string requestId, string expectedParentId)
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.UpstreamService.ExtractFromRequest = extractFromRequest))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultOperationParentId, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string responseRequestId = GetResponseHeader(response, DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(extractFromRequest, expectedParentId == correlation.OperationParentId);
                    Assert.Equal(extractFromRequest, requestId == responseRequestId);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendRequest_WithCorrelateOptionsOperationParentExtractFromRequest_UsesUpstreamOperationId(bool extractFromRequest)
        {
            // Arrange
            var operationParentId = Guid.NewGuid().ToString();
            var requestId = $"|{Guid.NewGuid()}.{operationParentId}";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.OperationParent.ExtractFromRequest = extractFromRequest))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultOperationParentId, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string responseRequestId = GetResponseHeader(response, DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(extractFromRequest, operationParentId == correlation.OperationParentId);
                    Assert.Equal(extractFromRequest, requestId == responseRequestId);
                }
            }
        }

        [Theory]
        [InlineData(false, "|abc.def", null)]
        [InlineData(true, "|abc.def", "def")]
        [InlineData(false, "abc", null)]
        [InlineData(true, "abc", "abc")]
        public async Task SendRequest_WithCorrelateOptionsOperationParentExtractFromRequest_UsesRequestId(bool extractFromRequest, string requestId, string expectedParentId)
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt => opt.OperationParent.ExtractFromRequest = extractFromRequest))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(DefaultOperationParentId, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string responseRequestId = GetResponseHeader(response, DefaultOperationParentId);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(extractFromRequest, expectedParentId == correlation.OperationParentId);
                    Assert.Equal(extractFromRequest, requestId == responseRequestId);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendRequest_WithCorrelateOptionsOperationParentCustomOperationParentIHeaderName_UsesUpstreamOperationId(bool extractFromRequest)
        {
            // Arrange
            var operationParentId = Guid.NewGuid().ToString();
            var requestId = $"|{Guid.NewGuid()}.{operationParentId}";
            var operationParentIdHeaderName = "My-Request-Id";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt =>
                {
                    opt.OperationParent.ExtractFromRequest = extractFromRequest;
                    opt.OperationParent.OperationParentIdHeaderName = operationParentIdHeaderName;
                }))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                              .Get(CorrelationController.GetRoute)
                              .WithHeader(operationParentIdHeaderName, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string responseRequestId = GetResponseHeader(response, operationParentIdHeaderName);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(extractFromRequest, operationParentId == correlation.OperationParentId);
                    Assert.Equal(extractFromRequest, responseRequestId == requestId);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendRequest_WithCorrelateOptionsUpstreamServiceCustomOperationParentIHeaderName_UsesUpstreamOperationId(bool extractFromRequest)
        {
            // Arrange
            var operationParentId = Guid.NewGuid().ToString();
            var requestId = $"|{Guid.NewGuid()}.{operationParentId}";
            var operationParentIdHeaderName = "My-Request-Id";
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation(opt =>
                {
                    opt.UpstreamService.ExtractFromRequest = extractFromRequest;
                    opt.UpstreamService.OperationParentIdHeaderName = operationParentIdHeaderName;
                }))
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder
                    .Get(CorrelationController.GetRoute)
                    .WithHeader(operationParentIdHeaderName, requestId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    string transactionId = GetResponseHeader(response, DefaultTransactionId);
                    string responseRequestId = GetResponseHeader(response, operationParentIdHeaderName);

                    CorrelationInfo correlation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.NotEmpty(correlation.OperationId);
                    Assert.Equal(transactionId, correlation.TransactionId);
                    Assert.Equal(extractFromRequest, operationParentId == correlation.OperationParentId);
                    Assert.Equal(extractFromRequest, requestId == responseRequestId);
                }
            }
        }

        [Fact]
        public async Task SendRequestWithCorrelateInfo_SetsCorrelationInfo_ResponseWithUpdatedCorrelationInfo()
        {
            // Arrange
            var correlationInfo = new CorrelationInfo($"operation-{Guid.NewGuid()}", $"transaction-{Guid.NewGuid()}", $"parent{Guid.NewGuid()}".Replace("-", ""));
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation())
                .PreConfigure(app => app.UseHttpCorrelation());
            
            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Post(CorrelationController.SetCorrelationRoute)
                    .WithHeader(DefaultOperationId, correlationInfo.OperationId)
                    .WithHeader(DefaultTransactionId, correlationInfo.TransactionId)
                    .WithHeader(DefaultOperationParentId, correlationInfo.OperationParentId);

                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    string actualTransactionId = GetResponseHeader(response, DefaultTransactionId);
                    string actualParentId = GetResponseHeader(response, DefaultOperationParentId);

                    Assert.Equal(correlationInfo.TransactionId, actualTransactionId);
                    Assert.Equal(correlationInfo.OperationParentId, actualParentId);

                    CorrelationInfo actualCorrelation = await ReadCorrelationInfoFromResponseBodyAsync(response);
                    Assert.Equal(correlationInfo.OperationId, actualCorrelation.OperationId);
                    Assert.Equal(correlationInfo.TransactionId, actualCorrelation.TransactionId);
                    Assert.Equal(correlationInfo.OperationParentId, actualCorrelation.OperationParentId);
                }
            }
        }

        [Fact]
        public async Task SendRequestWithAlternateCorrelationAccessor_RemovesCorrelation_ResponseWithoutCorrelationInfo()
        {
            // Arrange
            var options = new TestApiServerOptions()
                .ConfigureServices(services => services.AddHttpCorrelation()
                                                       .AddScoped<IHttpCorrelationInfoAccessor, NullCorrelationInfoAccessor>())
                .PreConfigure(app => app.UseHttpCorrelation());

            await using (var server = await TestApiServer.StartNewAsync(options, _logger))
            {
                var request = HttpRequestBuilder.Get(EchoController.GetPostRoute)
                    .WithHeader(DefaultOperationParentId, $"operation{Guid.NewGuid()}".Replace("-", ""))
                    .WithHeader(DefaultTransactionId, $"transaction-{Guid.NewGuid()}");
                
                // Act
                using (HttpResponseMessage response = await server.SendAsync(request))
                {
                    // Assert
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Contains(response.Headers, header => header.Key == DefaultOperationParentId);
                    Assert.DoesNotContain(response.Headers, header => header.Key == DefaultTransactionId);
                }
            }
        }

        private static string GetResponseHeader(HttpResponseMessage response, string headerName)
        {
            (string key, IEnumerable<string> values) = Assert.Single(response.Headers, header => header.Key == headerName);
            
            Assert.NotNull(values);
            string value = Assert.Single(values);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Response header '{headerName}' cannot be blank");

            return value;
        }

        private static async Task<CorrelationInfo> ReadCorrelationInfoFromResponseBodyAsync(HttpResponseMessage response)
        {
            string json = await response.Content.ReadAsStringAsync();
            var content = JsonConvert.DeserializeAnonymousType(json, new { OperationId = "", TransactionId = "", OperationParentId = "" });

            return new CorrelationInfo(
                content.OperationId,
                content.TransactionId,
                content.OperationParentId);
        }
    }
}
