using Pulumi;
using Pulumi.Azure.Compute;
using Pulumi.Azure.Compute.Inputs;
using Pulumi.Azure.ContainerService;
using Pulumi.Azure.ContainerService.Inputs;
using Pulumi.Azure.Core;
using Pulumi.Azure.Network;
using Pulumi.Azure.Network.Inputs;
using Pulumi.Azure.PrivateDns;
using Pulumi.Azure.Storage;

class RabbitMqStack : Stack
{
    private readonly string _location;
    private readonly string _privateDnsZoneName;

    private const int ContainerStartupDelay = 60;

    public RabbitMqStack()
    {
        Config config = new Config();
        _location = config.Require(ConfigKey.Location);
        _privateDnsZoneName = config.Require(ConfigKey.PrivateDnsZoneName);

        Stack();
    }

    [Output]
    public Output<string> RmqFqdn { get; private set; } = Output.Create(string.Empty);
    
    [Output]
    public Output<string?> vmIp { get; private set; } = Output.Create(string.Empty);

    private void Stack()
    {
        // Create the resource groups that holds resources pertaining to this stack.
        var dnsResourceGroup = ResourceGroup("rg-dns");
        var rmqResourceGroup = ResourceGroup("rg-rmq");
        var vmResourceGroup  = ResourceGroup("rg-vm");

        // Create the containerized rabbitmq service and it's networking configuration.
        var storage = Storage("rmqsa", rmqResourceGroup);
        var rmqVNet = VNet("vnet-rmq", new[]{ "10.2.0.0/16" }, rmqResourceGroup);
        var rmqSNet = ContainerSNet("snet-rmq", new[]{ "10.2.0.0/16" }, rmqResourceGroup, rmqVNet);
        var networkProfile = NetworkProfile("np-rmq", rmqResourceGroup, rmqSNet);
        var container = Container("aci-rmq", rmqResourceGroup, storage, networkProfile.Id, "LHPHUKALTJYRFIZQGLTQ", null);

        // Create a test VM in a separate resource group.
        var vmVNet = VNet("vnet-vm", new[]{ "10.1.0.0/16" }, vmResourceGroup);
        var vmSNet = SNet("snet-vm", new[]{ "10.1.0.0/16" }, vmResourceGroup, vmVNet);
        var pip = Pip("pip-vm", vmResourceGroup);
        var networkInterface = NetworkInterface("ni-vm", vmSNet, pip, vmResourceGroup);
        var vm = Vm("vm-service", vmResourceGroup, pip, networkInterface, vmVNet, vmSNet);

        // Create an NSG and associate it to the VM's attached network interface.
        _ = new NetworkInterfaceSecurityGroupAssociation("sNet-nsg-vm", new NetworkInterfaceSecurityGroupAssociationArgs
        {
            NetworkSecurityGroupId = NsgDefaultRules("nsg-vm", vmResourceGroup).Id,
            NetworkInterfaceId = networkInterface.Id,
        });

        // Create an NSG and associate it to the container instance subnet.
        _ = new SubnetNetworkSecurityGroupAssociation("sNet-nsg-vm", new SubnetNetworkSecurityGroupAssociationArgs
        {
            NetworkSecurityGroupId = NsgDefaultRules("nsg-rmq", rmqResourceGroup).Id,
            SubnetId = rmqSNet.Id,
        });

        // Configure vNet Peering between the test VM and the RabbitMQ service.
        VNetPeering("vnp-vm", vmResourceGroup.Name, vmVNet.Name, rmqVNet);
        VNetPeering("vnp-rmq", rmqResourceGroup.Name, rmqVNet.Name, vmVNet);

        // Create a private DNS zone, add a record for to enable private resolution of the RMQ container 
        // and link the DNS zone to both the rabbitMQ and test VM's virtual networks.
        var privateDns = PrivateDns(dnsResourceGroup.Name);
        var record = DnsRecord("a-rmq", dnsResourceGroup.Name, privateDns, container.IpAddress);
        ZoneLink("zoneline-rmq", dnsResourceGroup.Name, privateDns, rmqVNet.Id);
        ZoneLink("zonelink-vm", dnsResourceGroup.Name, privateDns, vmVNet.Id);

        // Set IaC outputs.
        this.RmqFqdn = record.Fqdn; 
        this.vmIp = pip.IpAddress;
    }

    private ResourceGroup ResourceGroup(string name)
    {
        return new ResourceGroup(name, new ResourceGroupArgs
        {
            Location = _location
        });
    }

    private Account Storage(string name, ResourceGroup resourceGroup)
    {
        return new Account($"sa{name}", new AccountArgs {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            AccountKind = "StorageV2",
            AccountReplicationType = "LRS",
            AccountTier = "Standard"
        });
    }

    private Group Container(string name, ResourceGroup resourceGroup, Account storage, Output<string> networkProfileId, string cookie, Resource[]? dependencies)
    {
        return new Group(name, new GroupArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            IpAddressType = "Private",
            NetworkProfileId = networkProfileId,
            OsType = "Linux",
            Containers =
            {
                new GroupContainerArgs
                {
                    Name = "rabbitmq",
                    Image = "rabbitmq",
                    Commands =
                    {
                        "/bin/bash",
                        "-c",
                        $"(sleep {ContainerStartupDelay} && docker-entrypoint.sh rabbitmq-server) & wait"
                    },
                    Cpu = 1,
                    Memory = 1.5,
                    Volumes =
                    {
                        ContainerVolume("vol-rmq-config", storage, "/var/lib/rabbitmq/config"),
                        ContainerVolume("vol-rmq-mnesia", storage, "/var/lib/rabbitmq/mnesia"),
                        ContainerVolume("vol-rmq-schema", storage, "/var/lib/rabbitmq/schema"),
                    },
                    Ports =
                    {
                        ContainerPort(15672, "TCP"),
                        ContainerPort(25672, "TCP"),
                        ContainerPort(5672, "TCP"),
                        ContainerPort(4369, "TCP")
                    },
                    EnvironmentVariables =
                    {
                        { "RABBITMQ_ERLANG_COOKIE", cookie },
                        { "RABBITMQ_NODENAME", $"rabbit@rmq.{_privateDnsZoneName}" },
                        { "RABBITMQ_USE_LONGNAME", "true" }
                    }
                }
            }
        });
    }
    
    private GroupContainerVolumeArgs ContainerVolume(string name, Account storageAccount, string mountPath) =>
        new GroupContainerVolumeArgs
        {
            Name = name,
            MountPath = mountPath,
            StorageAccountName = storageAccount.Name,
            StorageAccountKey = storageAccount.PrimaryAccessKey,
            ShareName = FileShare(storageAccount, name).Name
        };

    private GroupContainerPortArgs ContainerPort(int port, string protocol)
    {
        return new GroupContainerPortArgs
        {
            Port = port,
            Protocol = protocol
        };
    }

    private Share FileShare(Account storageAccount, string shareName)
    {
        return new Share(shareName, new ShareArgs
        {
            StorageAccountName = storageAccount.Name
        });
    }

    private Profile NetworkProfile(string name, ResourceGroup resourceGroup, Subnet inSNet)
    {
        return new Profile(name, new ProfileArgs
        {
            Location = resourceGroup.Location,
            ResourceGroupName = resourceGroup.Name,
            ContainerNetworkInterface = new ProfileContainerNetworkInterfaceArgs
            {
                Name = $"{name}-nic",
                IpConfigurations =
                {
                    new ProfileContainerNetworkInterfaceIpConfigurationArgs 
                    {
                        Name = $"{name}-ipconfig",
                        SubnetId = inSNet.Id
                    }
                }
            }
        });
    }

    private Subnet ContainerSNet(string name, InputList<string> addressPrefixs, ResourceGroup resourceGroup, VirtualNetwork vNet)
    {
        return new Subnet(name, new SubnetArgs
        {
            ResourceGroupName = resourceGroup.Name,
            VirtualNetworkName = vNet.Name,
            AddressPrefixes = addressPrefixs,
            ServiceEndpoints = { "Microsoft.Storage" },
            Delegations = new SubnetDelegationArgs()
            {
                Name = $"snet-delegation-{name}",
                ServiceDelegation = new SubnetDelegationServiceDelegationArgs
                {
                    Name = "Microsoft.ContainerInstance/containerGroups"
                }
            }
        });
    }

    private Subnet SNet(string name, InputList<string> addressPrefixs, ResourceGroup resourceGroup, VirtualNetwork vNet)
    {
        return new Subnet(name, new SubnetArgs
        {
            ResourceGroupName = resourceGroup.Name,
            VirtualNetworkName = vNet.Name,
            AddressPrefixes = addressPrefixs,
        });
    }

    private VirtualNetwork VNet(string name, InputList<string> addressSpaces, ResourceGroup resourceGroup)
    {
        return new VirtualNetwork(name, new VirtualNetworkArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AddressSpaces = addressSpaces,
        });
    }

    private VirtualNetworkPeering VNetPeering(string name, Output<string> resourceGroupName, Output<string> vNetName, VirtualNetwork vNet2)
    {
        return new VirtualNetworkPeering(name, new VirtualNetworkPeeringArgs
        {
            ResourceGroupName = resourceGroupName,
            VirtualNetworkName = vNetName,
            AllowForwardedTraffic = true,
            RemoteVirtualNetworkId = vNet2.Id,
        });
    }

    private Zone PrivateDns(Output<string> resourceGroupName)
    {
        return new Zone(_privateDnsZoneName, new ZoneArgs
        {
            Name = _privateDnsZoneName,
            ResourceGroupName = resourceGroupName
        });
    }

    private ARecord DnsRecord(string name, Output<string> resourceGroupName, Zone privateDns, Output<string> ipAddress)
    {
        return new ARecord(name, new ARecordArgs
        {
            ResourceGroupName = resourceGroupName,
            ZoneName = privateDns.Name,
            Name = name,
            Records = { ipAddress },
            Ttl = 300
        });
    }

    private void ZoneLink(string name, Output<string> resourceGroupName, Zone privateDns, Output<string> vNetId)
    {
        var zoneLink = new ZoneVirtualNetworkLink(name, new ZoneVirtualNetworkLinkArgs
        {
            ResourceGroupName = resourceGroupName,
            PrivateDnsZoneName = privateDns.Name,
            VirtualNetworkId = vNetId,
            RegistrationEnabled = false
        });
    }

    private PublicIp Pip(string name, ResourceGroup resourceGroup)
    {
        return new PublicIp(name, new PublicIpArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            AllocationMethod = "Dynamic",
        });
    }

    private NetworkInterface NetworkInterface(string name, Subnet sNet, PublicIp pip, ResourceGroup resourceGroup)
    {
        return new NetworkInterface(name, new NetworkInterfaceArgs
        {
            Location = resourceGroup.Location,
            ResourceGroupName = resourceGroup.Name,
            IpConfigurations = 
            {
                new NetworkInterfaceIpConfigurationArgs
                {
                    Name = "internal",
                    SubnetId = sNet.Id,
                    PrivateIpAddressAllocation = "Dynamic",
                    PublicIpAddressId = pip.Id
                },
            },
        });
    }

    private NetworkSecurityGroupSecurityRuleArgs NsgRule(
        string name,
        Input<string> sourceAddressPrefix,
        Input<string> sourcePortRange, 
        InputList<string> destinationAddressPrefix, 
        InputList<string> destinationPortRange, 
        Input<string> access, 
        Input<string> direction, 
        Input<string> protocol, 
        Input<int> priority)
    {
        return new NetworkSecurityGroupSecurityRuleArgs
        {
            Name = name,
            SourceAddressPrefix = sourceAddressPrefix,
            SourcePortRange = sourcePortRange,

            DestinationAddressPrefixes = destinationAddressPrefix,
            DestinationPortRanges = destinationPortRange,

            Access = access,
            Direction = direction,
            Protocol = protocol,
            Priority = priority,
        };
    }


    private NetworkSecurityGroup NsgDefaultRules(string name, ResourceGroup resourceGroup)
    {
        return new NetworkSecurityGroup(name, new NetworkSecurityGroupArgs
        {
            Location = resourceGroup.Location,
            ResourceGroupName = resourceGroup.Name,
        });
    }

    private LinuxVirtualMachine Vm(string name, ResourceGroup resourceGroup, PublicIp publicIp, NetworkInterface networkInterface, VirtualNetwork virtualNetwork, Subnet subnet) 
    {
        return new LinuxVirtualMachine(name, new LinuxVirtualMachineArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            Size = "Standard_B2s",
            AdminUsername = "adminuser",
            AdminPassword = "Password1234!",
            DisablePasswordAuthentication = false,
            NetworkInterfaceIds = 
            {
                networkInterface.Id,
            },
            OsDisk = new LinuxVirtualMachineOsDiskArgs
            {
                Caching = "ReadWrite",
                StorageAccountType = "Standard_LRS",
            },
            SourceImageReference = new LinuxVirtualMachineSourceImageReferenceArgs
            {
                Publisher = "Canonical",
                Offer = "UbuntuServer",
                Sku = "16.04-LTS",
                Version = "latest",
            },
        });
    }
}