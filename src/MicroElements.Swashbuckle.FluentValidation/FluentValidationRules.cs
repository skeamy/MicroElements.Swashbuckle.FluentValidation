using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MicroElements.Swashbuckle.FluentValidation
{
    /// <summary>
    /// Swagger <see cref="ISchemaFilter"/> that uses FluentValidation validators instead System.ComponentModel based attributes.
    /// </summary>
    public class FluentValidationRules : ISchemaFilter
    {
        private readonly IValidatorFactory _validatorFactory;
        private readonly ILogger _logger;
        private readonly IReadOnlyList<FluentValidationRule> _rules;

        /// <summary>
        /// Creates new instance of <see cref="FluentValidationRules"/>
        /// </summary>
        /// <param name="validatorFactory">Validator factory.</param>
        /// <param name="rules">External FluentValidation rules. Rule with the same name replaces default rule.</param>
        /// <param name="loggerFactory"><see cref="ILoggerFactory"/> for logging. Can be null.</param>
        public FluentValidationRules(
            [CanBeNull] IValidatorFactory validatorFactory = null,
            [CanBeNull] IEnumerable<FluentValidationRule> rules = null,
            [CanBeNull] ILoggerFactory loggerFactory = null)
        {
            _validatorFactory = validatorFactory;
            _logger = loggerFactory?.CreateLogger(typeof(FluentValidationRules)) ?? NullLogger.Instance;
            _rules = CreateDefaultRules().OverrideRules(rules);
        }

        /// <inheritdoc />
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (_validatorFactory == null)
            {
                _logger.LogWarning(0, "ValidatorFactory is not provided. Please register FluentValidation.");
                return;
            }

            IValidator validator = null;
            try
            {       
                validator = _validatorFactory.GetValidator(context.ApiModel.Type);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"GetValidator for type '{context.ApiModel.Type}' fails.");
            }

            if (validator == null)
                return;

            ApplyRulesToSchema(schema, context, validator);

            try
            {
                AddRulesFromIncludedValidators(schema, context, validator);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"Applying IncludeRules for type '{context.ApiModel.Type}' fails.");
            }
        }

        private void ApplyRulesToSchema(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            var lazyLog = new LazyLog(_logger,
                logger => logger.LogDebug($"Applying FluentValidation rules to swagger schema for type '{context.ApiModel.Type}'."));

            foreach (var schemaPropertyName in schema?.Properties?.Keys ?? Array.Empty<string>())
            {
                var validators = validator.GetValidatorsForMemberIgnoreCase(schemaPropertyName);

                foreach (var propertyValidator in validators)
                {
                    foreach (var rule in _rules)
                    {
                        if (rule.Matches(propertyValidator))
                        {
                            try
                            {
                                lazyLog.LogOnce();
                                rule.Apply(new RuleContext(schema, context, schemaPropertyName, propertyValidator));
                                _logger.LogDebug($"Rule '{rule.Name}' applied for property '{context.ApiModel.Type.Name}.{schemaPropertyName}'");
                            }
                            catch (Exception e)
                            {
                                _logger.LogWarning(0, e, $"Error on apply rule '{rule.Name}' for property '{context.ApiModel.Type.Name}.{schemaPropertyName}'.");
                            }
                        }
                    }
                }
            }
        }

        private void AddRulesFromIncludedValidators(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            // Note: IValidatorDescriptor doesn't return IncludeRules so we need to get validators manually.
            var childAdapters = (validator as IEnumerable<IValidationRule>)
                .NotNull()
                .OfType<IncludeRule>()
                .Where(includeRule => includeRule.Condition == null && includeRule.AsyncCondition == null)
                .SelectMany(includeRule => includeRule.Validators)
                .OfType<ChildValidatorAdaptor>();

            foreach (var adapter in childAdapters)
            {
                var propertyValidatorContext = new PropertyValidatorContext(new ValidationContext(null), null, string.Empty);
                var includeValidator = adapter.GetValidator(propertyValidatorContext);
                ApplyRulesToSchema(schema, context, includeValidator);
                AddRulesFromIncludedValidators(schema, context, includeValidator);
            }
        }

        /// <summary>
        /// Creates default rules.
        /// Can be overriden by name.
        /// </summary>
        public static FluentValidationRule[] CreateDefaultRules()
        {
            return new[]
            {
                new FluentValidationRule("NotListed")
                {
                    Matches = propertyValidator => !(propertyValidator is INotNullValidator
                                                    || propertyValidator is INotEmptyValidator
                                                    || propertyValidator is ILengthValidator
                                                    || propertyValidator is IRegularExpressionValidator
                                                    || propertyValidator is IComparisonValidator
                                                    || propertyValidator is IBetweenValidator
                                                    ),
                    Apply = context =>
                    {
                        addMessage(context);
                    }
                },
                new FluentValidationRule("Required")
                {
                    Matches = propertyValidator => propertyValidator is INotNullValidator || propertyValidator is INotEmptyValidator,
                    Apply = context =>
                    {
                        if (context.Schema.Required == null)
                            context.Schema.Required = new SortedSet<string>();
                        if(!context.Schema.Required.Contains(context.PropertyKey))
                        {
                            context.Schema.Required.Add(context.PropertyKey);
                            addMessage(context);
                        }
                    }
                },
                new FluentValidationRule("NotEmpty")
                {
                    Matches = propertyValidator => propertyValidator is INotEmptyValidator,
                    Apply = context =>
                    {
                        context.Schema.Properties[context.PropertyKey].MinLength = 1;
                        addMessage(context);
                    }
                },
                new FluentValidationRule("Length")
                {
                    Matches = propertyValidator => propertyValidator is ILengthValidator,
                    Apply = context =>
                    {
                        var lengthValidator = (ILengthValidator)context.PropertyValidator;

                        if(lengthValidator.Max > 0)
                        {
                            context.Schema.Properties[context.PropertyKey].MaxLength = lengthValidator.Max;
                        }

                        if (lengthValidator is MinimumLengthValidator
                            || lengthValidator is ExactLengthValidator
                            || context.Schema.Properties[context.PropertyKey].MinLength == null)
                        {
                            context.Schema.Properties[context.PropertyKey].MinLength = lengthValidator.Min;
                        }
                        addMessage(context);
                    }
                },
                new FluentValidationRule("Pattern")
                {
                    Matches = propertyValidator => propertyValidator is IRegularExpressionValidator,
                    Apply = context =>
                    {
                        var regularExpressionValidator = (IRegularExpressionValidator)context.PropertyValidator;
                        context.Schema.Properties[context.PropertyKey].Pattern = regularExpressionValidator.Expression;
                        addMessage(context);
                    }
                },
                new FluentValidationRule("Comparison")
                {
                    Matches = propertyValidator => propertyValidator is IComparisonValidator,
                    Apply = context =>
                    {
                        var comparisonValidator = (IComparisonValidator)context.PropertyValidator;
                        if (comparisonValidator.ValueToCompare.IsNumeric())
                        {
                            var valueToCompare = comparisonValidator.ValueToCompare.NumericToDouble();
                            var schemaProperty = context.Schema.Properties[context.PropertyKey];

                            if (comparisonValidator.Comparison == Comparison.GreaterThanOrEqual)
                            {
                                schemaProperty.Minimum = (decimal?) valueToCompare;
                            }
                            else if (comparisonValidator.Comparison == Comparison.GreaterThan)
                            {
                                schemaProperty.Minimum = (decimal?) valueToCompare;
                                schemaProperty.ExclusiveMinimum = true;
                            }
                            else if (comparisonValidator.Comparison == Comparison.LessThanOrEqual)
                            {
                                schemaProperty.Maximum = (decimal?) valueToCompare;
                            }
                            else if (comparisonValidator.Comparison == Comparison.LessThan)
                            {
                                schemaProperty.Maximum = (decimal?) valueToCompare;
                                schemaProperty.ExclusiveMaximum = true;
                            }
                        }
                        addMessage(context);
                    }
                },
                new FluentValidationRule("Between")
                {
                    Matches = propertyValidator => propertyValidator is IBetweenValidator,
                    Apply = context =>
                    {
                        var betweenValidator = (IBetweenValidator)context.PropertyValidator;
                        var schemaProperty = context.Schema.Properties[context.PropertyKey];

                        if (betweenValidator.From.IsNumeric())
                        {
                            schemaProperty.Minimum = (decimal?) betweenValidator.From.NumericToDouble();

                            if (betweenValidator is ExclusiveBetweenValidator)
                            {
                                schemaProperty.ExclusiveMinimum = true;
                            }
                        }

                        if (betweenValidator.To.IsNumeric())
                        {
                            schemaProperty.Maximum = (decimal?) betweenValidator.To.NumericToDouble();

                            if (betweenValidator is ExclusiveBetweenValidator)
                            {
                                schemaProperty.ExclusiveMaximum = true;
                            }
                        }
                        addMessage(context);
                    }
                },
            };
        }
        private static void addMessage(RuleContext context)
        {
            var message = context.PropertyValidator.Options.ErrorMessageSource.GetString(new ValidationContext(null));
            var title = "\n\n*Validation Rules*\n\n";
            if (context.Schema.Properties[context.PropertyKey] != null
                && !string.IsNullOrWhiteSpace(context.Schema.Properties[context.PropertyKey].Description))
            {
                if (context.Schema.Properties[context.PropertyKey].Description.IndexOf(title) > -1)
                {
                    message = "\n\n" + message;
                }
                else
                {
                    message = title + message;
                }
            }
            else
            {
                message = title + message;
            }
            var friendlyName = string.Concat((string.Concat(context.PropertyKey.First().ToString().ToUpper() + context.PropertyKey.Substring(1))).Select(x => Char.IsUpper(x) ? " " + x : x.ToString())).TrimStart(' ');

            message = message.Replace("{PropertyName}", friendlyName);
            if (context.Schema.Properties[context.PropertyKey].MaxLength.HasValue)
            {
                message = message.Replace("{MaxLength}", context.Schema.Properties[context.PropertyKey].MaxLength.Value.ToString());
            }
            if (context.Schema.Properties[context.PropertyKey].MinLength.HasValue)
            {
                message = message.Replace("{MinLength}", context.Schema.Properties[context.PropertyKey].MinLength.Value.ToString());
            }
            if (context.Schema.Properties[context.PropertyKey].Maximum.HasValue)
            {
                message = message.Replace("{Maximum}", context.Schema.Properties[context.PropertyKey].Maximum.Value.ToString());
            }
            if (context.Schema.Properties[context.PropertyKey].Minimum.HasValue)
            {
                message = message.Replace("{Minimum}", context.Schema.Properties[context.PropertyKey].Minimum.Value.ToString());
            }
            message = message.Replace("{TotalLength}", "x");

            if (context.Schema.Properties[context.PropertyKey].Description == null || context.Schema.Properties[context.PropertyKey].Description.IndexOf(message) < 0)
            {
                context.Schema.Properties[context.PropertyKey].Description += message;
            }
        }
    }
}
