﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Core;
using Autofac.Features.Variance;
using Confluent.Kafka;
using FluentValidation;
using MediatR;
using MediatR.Pipeline;
using Microsoft.Extensions.Configuration;
using Wire.Transfer.In.Application.Configuration.Validation;
using Wire.Transfer.In.Application.WireTransfers.RegisterTransfer;
using Wire.Transfer.In.Application.WireTransfers.RegisterTransfer.PublishOnTopic;
using Module = Autofac.Module;

namespace Wire.Transfer.In.Infrastructure.InProc
{
    public class InProcModule : Module
    {
        private readonly IConfiguration _configuration;

        public InProcModule(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        protected override void Load(ContainerBuilder builder)
        {
            RegisterKafka(builder);
            RegisterMediator(builder);
        }

        private void RegisterKafka(ContainerBuilder builder)
        {
            builder.Register(c => new KafkaConfiguration
            {
                BootstrapServers =
                    _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.BootstrapServers)}"],
                SaslUsername =
                    _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.SaslUsername)}"],
                SaslPassword =
                    _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.SaslPassword)}"],
                SecurityProtocol = (SecurityProtocol) Enum.Parse(typeof(SecurityProtocol),
                    _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.SecurityProtocol)}"]),
                TopicName = _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.TopicName)}"],
                MessageTimeoutMs =
                    Convert.ToInt32(
                        _configuration[$"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.MessageTimeoutMs)}"]),
                EnableSslCertificateVerification = Convert.ToBoolean(_configuration[
                    $"{nameof(KafkaConfiguration)}:{nameof(KafkaConfiguration.EnableSslCertificateVerification)}"])
            }).As<KafkaConfiguration>();
        }

        private static void RegisterMediator(ContainerBuilder builder)
        {
            builder.RegisterSource(new ScopedContravariantRegistrationSource(
                typeof(IRequestHandler<,>),
                typeof(INotificationHandler<>),
                typeof(IValidator<>)
            ));

            builder.RegisterAssemblyTypes(typeof(IMediator).GetTypeInfo().Assembly).AsImplementedInterfaces();

            var mediatrOpenTypes = new[]
            {
                typeof(IRequestHandler<,>),
                typeof(INotificationHandler<>),
                typeof(IValidator<>)
            };

            foreach (var mediatrOpenType in mediatrOpenTypes)
                builder
                    .RegisterAssemblyTypes(typeof(RegisterWireTransferCommand).GetTypeInfo().Assembly)
                    .AsClosedTypesOf(mediatrOpenType)
                    .AsImplementedInterfaces();

            builder.RegisterGeneric(typeof(RequestPostProcessorBehavior<,>)).As(typeof(IPipelineBehavior<,>));
            builder.RegisterGeneric(typeof(RequestPreProcessorBehavior<,>)).As(typeof(IPipelineBehavior<,>));

            builder.Register<ServiceFactory>(ctx =>
            {
                var c = ctx.Resolve<IComponentContext>();
                return t => c.Resolve(t);
            });

            builder.RegisterGeneric(typeof(CommandValidationBehavior<,>)).As(typeof(IPipelineBehavior<,>));
        }

        private class ScopedContravariantRegistrationSource : IRegistrationSource
        {
            private readonly IRegistrationSource _source = new ContravariantRegistrationSource();
            private readonly List<Type> _types = new List<Type>();

            public ScopedContravariantRegistrationSource(params Type[] types)
            {
                if (types == null)
                    throw new ArgumentNullException(nameof(types));
                if (!types.All(x => x.IsGenericTypeDefinition))
                    throw new ArgumentException("Supplied types should be generic type definitions");
                _types.AddRange(types);
            }

            public bool IsAdapterForIndividualComponents => _source.IsAdapterForIndividualComponents;

            public IEnumerable<IComponentRegistration> RegistrationsFor(
                Service service,
                Func<Service, IEnumerable<IComponentRegistration>> registrationAccessor)
            {
                var components = _source.RegistrationsFor(service, registrationAccessor);
                foreach (var c in components)
                {
                    var defs = c.Target.Services
                        .OfType<TypedService>()
                        .Select(x => x.ServiceType.GetGenericTypeDefinition());

                    if (defs.Any(_types.Contains))
                        yield return c;
                }
            }
        }
    }
}