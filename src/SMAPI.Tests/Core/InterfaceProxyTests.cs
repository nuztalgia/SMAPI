#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using SMAPI.Tests.ModApiConsumer;
using SMAPI.Tests.ModApiConsumer.Interfaces;
using SMAPI.Tests.ModApiProvider;
using StardewModdingAPI.Framework.Reflection;

namespace SMAPI.Tests.Core
{
    /// <summary>Unit tests for <see cref="InterfaceProxyFactory"/>.</summary>
    [TestFixture]
    internal class InterfaceProxyTests
    {
        /*********
        ** Fields
        *********/
        /// <summary>The mod ID providing an API.</summary>
        private readonly string FromModId = "From.ModId";

        /// <summary>The mod ID consuming an API.</summary>
        private readonly string ToModId = "From.ModId";

        /// <summary>The random number generator with which to create sample values.</summary>
        private readonly Random Random = new();


        /*********
        ** Unit tests
        *********/
        /****
        ** Events
        ****/
        /// <summary>Assert that an event field can be proxied correctly.</summary>
        [Test]
        public void CanProxy_EventField()
        {
            // arrange
            var providerMod = new ProviderMod();
            object implementation = providerMod.GetModApi();
            int expectedValue = this.Random.Next();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            new ApiConsumer().UseEventField(proxy, out Func<(int timesCalled, int lastValue)> getValues);
            providerMod.RaiseEvent(expectedValue);
            (int timesCalled, int lastValue) = getValues();

            // assert
            timesCalled.Should().Be(1, "Expected the proxied event to be raised once.");
            lastValue.Should().Be(expectedValue, "The proxy received a different event argument than the implementation raised.");
        }

        /// <summary>Assert that an event property can be proxied correctly.</summary>
        [Test]
        public void CanProxy_EventProperty()
        {
            // arrange
            var providerMod = new ProviderMod();
            object implementation = providerMod.GetModApi();
            int expectedValue = this.Random.Next();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            new ApiConsumer().UseEventProperty(proxy, out Func<(int timesCalled, int lastValue)> getValues);
            providerMod.RaiseEvent(expectedValue);
            (int timesCalled, int lastValue) = getValues();

            // assert
            timesCalled.Should().Be(1, "Expected the proxied event to be raised once.");
            lastValue.Should().Be(expectedValue, "The proxy received a different event argument than the implementation raised.");
        }

        /****
        ** Properties
        ****/
        /// <summary>Assert that properties can be proxied correctly.</summary>
        /// <param name="setVia">Whether to set the properties through the <c>provider mod</c> or <c>proxy interface</c>.</param>
        [TestCase("set via provider mod")]
        [TestCase("set via proxy interface")]
        public void CanProxy_Properties(string setVia)
        {
            // arrange
            var providerMod = new ProviderMod();
            object implementation = providerMod.GetModApi();
            int expectedNumber = this.Random.Next();
            int expectedObject = this.Random.Next();
            string expectedListValue = this.GetRandomString();
            string expectedListWithInterfaceValue = this.GetRandomString();
            string expectedDictionaryKey = this.GetRandomString();
            string expectedDictionaryListValue = this.GetRandomString();
            string expectedInheritedString = this.GetRandomString();
            BindingFlags expectedEnum = BindingFlags.Instance | BindingFlags.Public;

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            switch (setVia)
            {
                case "set via provider mod":
                    providerMod.SetPropertyValues(
                        number: expectedNumber,
                        obj: expectedObject,
                        listValue: expectedListValue,
                        listWithInterfaceValue: expectedListWithInterfaceValue,
                        dictionaryKey: expectedDictionaryKey,
                        dictionaryListValue: expectedDictionaryListValue,
                        enumValue: expectedEnum,
                        inheritedValue: expectedInheritedString
                    );
                    break;

                case "set via proxy interface":
                    proxy.NumberProperty = expectedNumber;
                    proxy.ObjectProperty = expectedObject;
                    proxy.ListProperty = new() { expectedListValue };
                    proxy.ListPropertyWithInterface = new List<string> { expectedListWithInterfaceValue };
                    proxy.GenericsProperty = new Dictionary<string, IList<string>>
                    {
                        [expectedDictionaryKey] = new List<string> { expectedDictionaryListValue }
                    };
                    proxy.EnumProperty = expectedEnum;
                    proxy.InheritedProperty = expectedInheritedString;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid 'set via' option '{setVia}.");
            }

            // assert number
            this
                .GetPropertyValue(implementation, nameof(proxy.NumberProperty))
                .Should().Be(expectedNumber);
            proxy.NumberProperty
                .Should().Be(expectedNumber);

            // assert object
            this
                .GetPropertyValue(implementation, nameof(proxy.ObjectProperty))
                .Should().Be(expectedObject);
            proxy.ObjectProperty
                .Should().Be(expectedObject);

            // assert list
            (this.GetPropertyValue(implementation, nameof(proxy.ListProperty)) as IList<string>)
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.BeEquivalentTo(expectedListValue);
            proxy.ListProperty
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.BeEquivalentTo(expectedListValue);

            // assert list with interface
            (this.GetPropertyValue(implementation, nameof(proxy.ListPropertyWithInterface)) as IList<string>)
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.BeEquivalentTo(expectedListWithInterfaceValue);
            proxy.ListPropertyWithInterface
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.BeEquivalentTo(expectedListWithInterfaceValue);

            // assert generics
            (this.GetPropertyValue(implementation, nameof(proxy.GenericsProperty)) as IDictionary<string, IList<string>>)
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.ContainKey(expectedDictionaryKey).WhoseValue.Should().BeEquivalentTo(expectedDictionaryListValue);
            proxy.GenericsProperty
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.ContainKey(expectedDictionaryKey).WhoseValue.Should().BeEquivalentTo(expectedDictionaryListValue);

            // assert enum
            this
                .GetPropertyValue(implementation, nameof(proxy.EnumProperty))
                .Should().Be(expectedEnum);
            proxy.EnumProperty
                .Should().Be(expectedEnum);

            // assert getter
            this
                .GetPropertyValue(implementation, nameof(proxy.GetterProperty))
                .Should().Be(42);
            proxy.GetterProperty
                .Should().Be(42);

            // assert inherited methods
            this
                .GetPropertyValue(implementation, nameof(proxy.InheritedProperty))
                .Should().Be(expectedInheritedString);
            proxy.InheritedProperty
                .Should().Be(expectedInheritedString);
        }

        /// <summary>Assert that a simple method with no return value can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_Void()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            proxy.GetNothing();
        }

        /// <summary>Assert that a simple int method can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_Int()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            int expectedValue = this.Random.Next();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            int actualValue = proxy.GetInt(expectedValue);

            // assert
            actualValue.Should().Be(expectedValue);
        }

        /// <summary>Assert that a simple object method can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_Object()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            object expectedValue = new();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            object actualValue = proxy.GetObject(expectedValue);

            // assert
            actualValue.Should().BeSameAs(expectedValue);
        }

        /// <summary>Assert that a simple list method can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_List()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            string expectedValue = this.GetRandomString();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            IList<string> actualValue = proxy.GetList(expectedValue);

            // assert
            actualValue.Should().BeEquivalentTo(expectedValue);
        }

        /// <summary>Assert that a simple list with interface method can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_ListWithInterface()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            string expectedValue = this.GetRandomString();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            IList<string> actualValue = proxy.GetListWithInterface(expectedValue);

            // assert
            actualValue.Should().BeEquivalentTo(expectedValue);
        }

        /// <summary>Assert that a simple method which returns generic types can be proxied correctly.</summary>
        [Test]
        public void CanProxy_SimpleMethod_GenericTypes()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            string expectedKey = this.GetRandomString();
            string expectedValue = this.GetRandomString();

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            IDictionary<string, IList<string>> actualValue = proxy.GetGenerics(expectedKey, expectedValue);

            // assert
            actualValue
                .Should().NotBeNull()
                .And.HaveCount(1)
                .And.ContainKey(expectedKey).WhoseValue.Should().BeEquivalentTo(expectedValue);
        }

        /// <summary>Assert that a simple lambda method can be proxied correctly.</summary>
        [Test]
        [SuppressMessage("ReSharper", "ConvertToLocalFunction")]
        public void CanProxy_SimpleMethod_Lambda()
        {
            // arrange
            object implementation = new ProviderMod().GetModApi();
            Func<string, string> expectedValue = _ => "test";

            // act
            ISimpleApi proxy = this.GetProxy(implementation);
            object actualValue = proxy.GetObject(expectedValue);

            // assert
            actualValue.Should().BeSameAs(expectedValue);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get a property value from an instance.</summary>
        /// <param name="parent">The instance whose property to read.</param>
        /// <param name="name">The property name.</param>
        private object GetPropertyValue(object parent, string name)
        {
            if (parent is null)
                throw new ArgumentNullException(nameof(parent));

            Type type = parent.GetType();
            PropertyInfo property = type.GetProperty(name);
            if (property is null)
                throw new InvalidOperationException($"The '{type.FullName}' type has no public property named '{name}'.");

            return property.GetValue(parent);
        }

        /// <summary>Get a random test string.</summary>
        private string GetRandomString()
        {
            return this.Random.Next().ToString();
        }

        /// <summary>Get a proxy API instance.</summary>
        /// <param name="implementation">The underlying API instance.</param>
        private ISimpleApi GetProxy(object implementation)
        {
            var proxyFactory = new InterfaceProxyFactory();
            return proxyFactory.CreateProxy<ISimpleApi>(implementation, this.FromModId, this.ToModId);
        }
    }
}
