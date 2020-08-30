using System.Collections.Generic;
using Amazon.CDK;
using CM = Amazon.CDK.AWS.CertificateManager;
using EC2 = Amazon.CDK.AWS.EC2;
using ECS = Amazon.CDK.AWS.ECS;
using ELB = Amazon.CDK.AWS.ElasticLoadBalancingV2;
using RDS = Amazon.CDK.AWS.RDS;
using R53 = Amazon.CDK.AWS.Route53;
using SM = Amazon.CDK.AWS.SecretsManager;
using Microsoft.Extensions.Configuration;

namespace PersonalCloud
{
    public class PersonalCloudStack : Stack
    {
        private readonly IConfiguration _configuration;

        internal PersonalCloudStack(IConfiguration configuration, Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            _configuration = configuration;

            var zone = CreateR53Zone();
            var vpc = CreateVpc();
            var ecsCluster = CreateEcsCluster(vpc);
            var juiceShop = CreateJuiceShop(ecsCluster, zone);
            var (database, databasePassword) = CreateMySQL(vpc);
            var nextcloud = CreateNextcloud(database, databasePassword, ecsCluster, zone);
        }

        private EC2.IVpc CreateVpc()
        {
            var vpc = new EC2.Vpc(this, "PersonalCloudVpc", new EC2.VpcProps
            {

            });

            return vpc;
        }

        private R53.IHostedZone CreateR53Zone()
        {
            var domainName = _configuration.GetValue<string>("AWS:Route53:DomainName");

            var zone = new R53.PublicHostedZone(this, "PersonalCloudR53Zone", new R53.PublicHostedZoneProps
            {
                ZoneName = domainName
            });

            return zone;
        }

        private ECS.ICluster CreateEcsCluster(EC2.IVpc vpc)
        {
            var ecsCluster = new ECS.Cluster(this, "PersonalCloudEcs", new ECS.ClusterProps
            {
                ClusterName = "PersonalCloudEcs",
                Vpc = vpc
            });

            return ecsCluster;
        }

        private (RDS.IDatabaseInstance, SM.ISecret) CreateMySQL(EC2.IVpc vpc)
        {
            var allocatedStorage = _configuration.GetValue<double>("AWS:RDS:MySQL:AllocatedStorage");
            var databaseName = _configuration.GetValue<string>("AWS:RDS:MySQL:DatabaseName");
            var userName = _configuration.GetValue<string>("AWS:RDS:MySQL:MasterUser:Username");
            var port = _configuration.GetValue<int>("AWS:RDS:MySQL:Port");
            var passwordLength = _configuration.GetValue<int>("AWS:RDS:MySQL:MasterUser:PasswordLength");
            var preferredMaintenanceWindow = _configuration.GetValue<string>("AWS:RDS:MySQL:PreferredMaintenanceWindow");
            var preferredBackupWindow = _configuration.GetValue<string>("AWS:RDS:MySQL:Backup:PreferredWindow");
            var backupRetentionDays = _configuration.GetValue<int>("AWS:RDS:MySQL:Backup:RetentionDays");
            var removalPolicy = _configuration.GetValue<RemovalPolicy>("AWS:RDS:MySQL:RemovalPolicy");
            var instanceClass = _configuration.GetValue<EC2.InstanceClass>("AWS:RDS:MySQL:InstanceClass");
            var instanceSize = _configuration.GetValue<EC2.InstanceSize>("AWS:RDS:MySQL:InstanceSize");
            var storageEncrypted = _configuration.GetValue<bool>("AWS:RDS:MySQL:StorageEncrypted");

            var password = new SM.Secret(this, "PersonalCloudRdsMySqlPassword", new SM.SecretProps
            {
                SecretName = "MySqlMasterUserPassword",
                GenerateSecretString = new SM.SecretStringGenerator
                {
                    ExcludeCharacters = "/@\"",
                    IncludeSpace = false,
                    PasswordLength = passwordLength
                }
            });

            var securityGroup = new EC2.SecurityGroup(this, "PersonalCloudRdsMySqlSecurityGroup", new EC2.SecurityGroupProps
            {   
                Description = "RDS MySQL",
                Vpc = vpc
            });

            securityGroup.AddEgressRule(EC2.Peer.AnyIpv4(), EC2.Port.AllTraffic(), "All traffic");
            securityGroup.AddIngressRule(EC2.Peer.AnyIpv4(), EC2.Port.Tcp(port), "MySQL");

            var database = new RDS.DatabaseInstance(this, "PersonalCloudRdsMySqlInstance", new RDS.DatabaseInstanceProps
            {
                AllocatedStorage = allocatedStorage,
                BackupRetention = Duration.Days(backupRetentionDays),
                DatabaseName = databaseName,
                DeletionProtection = false,
                Engine = RDS.DatabaseInstanceEngine.MYSQL,
                EngineVersion = "8.0.16",
                InstanceClass = EC2.InstanceType.Of(instanceClass, instanceSize),
                MasterUsername = userName,
                MasterUserPassword = password.SecretValue,
                ParameterGroup = new RDS.ParameterGroup(this, "PersonalCloudRdsMySqlParamGroup", new RDS.ParameterGroupProps
                {
                    Family = "mysql8.0",
                    Parameters = new Dictionary<string, string>
                    {
                        // Enable MySQL 4-byte support
                        // https://docs.nextcloud.com/server/17/admin_manual/configuration_database/mysql_4byte_support.html
                        { "innodb_file_per_table", "1" }
                    }
                }),
                Port = port,
                PreferredBackupWindow = preferredBackupWindow,
                PreferredMaintenanceWindow = preferredMaintenanceWindow,
                RemovalPolicy = removalPolicy,
                SecurityGroups = new EC2.ISecurityGroup[]
                {
                    securityGroup
                },
                StorageEncrypted = storageEncrypted,
                StorageType = RDS.StorageType.GP2,
                Vpc = vpc
            });

            return (database, password);
        }

        private ECS.Patterns.ApplicationLoadBalancedFargateService CreateNextcloud(RDS.IDatabaseInstance database, SM.ISecret databasePassword, ECS.ICluster ecsCluster, R53.IHostedZone zone)
        {
            var containerImage = _configuration.GetValue<string>("Nextcloud:ContainerImage");
            var desiredCount = _configuration.GetValue<int>("Nextcloud:DesiredCount");
            var domainName = _configuration.GetValue<string>("AWS:Route53:DomainName");
            var subdomainName = _configuration.GetValue<string>("Nextcloud:SubdomainName");
            var hostname = $"{subdomainName}.{domainName}";
            var certificateArn = _configuration.GetValue<string>("AWS:CertificateManager:CertificateArn");
            var databaseUsername = _configuration.GetValue<string>("AWS:RDS:MySQL:MasterUser:Username");
            var memoryLimitMiB = _configuration.GetValue<int>("Nextcloud:MemoryLimitMiB");
            var databaseName = _configuration.GetValue<string>("Nextcloud:DatabaseName");
            var adminUsername = _configuration.GetValue<string>("Nextcloud:AdminUser:Username");
            var adminPasswordLength = _configuration.GetValue<int>("Nextcloud:AdminUser:PasswordLength");

            var adminPassword = new SM.Secret(this, "PersonalCloudNextcloudAdminPassword", new SM.SecretProps
            {
                SecretName = "NextcloudAdminPassword",
                GenerateSecretString = new SM.SecretStringGenerator
                {
                    ExcludeCharacters = "/@\"",
                    IncludeSpace = false,
                    PasswordLength = adminPasswordLength
                }
            });

            var nextcloudService = new ECS.Patterns.ApplicationLoadBalancedFargateService(this, "NextcloudService", new ECS.Patterns.ApplicationLoadBalancedFargateServiceProps
            {
                Certificate = CM.Certificate.FromCertificateArn(this, "Certificate", certificateArn),
                Cluster = ecsCluster,
                DesiredCount = desiredCount,
                DomainName = hostname,
                DomainZone = zone,
                MemoryLimitMiB = memoryLimitMiB,
                Protocol = ELB.ApplicationProtocol.HTTPS,
                ServiceName = "NextcloudService",
                TaskImageOptions = new ECS.Patterns.ApplicationLoadBalancedTaskImageOptions
                {
                    ContainerName = "Nextcloud",
                    Environment = new Dictionary<string, string>
                    {
                        { "MYSQL_DATABASE", databaseName },
                        { "MYSQL_USER", databaseUsername },
                        { "MYSQL_HOST", $"{database.DbInstanceEndpointAddress}:{database.DbInstanceEndpointPort}" },
                        { "NEXTCLOUD_TRUSTED_DOMAINS", hostname },
                        { "NEXTCLOUD_ADMIN_USER", adminUsername }
                    },
                    Image = ECS.ContainerImage.FromRegistry(containerImage),
                    Secrets = new Dictionary<string, ECS.Secret>
                    {
                        { "MYSQL_PASSWORD", ECS.Secret.FromSecretsManager(databasePassword) },
                        { "NEXTCLOUD_ADMIN_PASSWORD", ECS.Secret.FromSecretsManager(adminPassword)}
                    }
                }
            });

            return nextcloudService;
        }

        private ECS.Patterns.ApplicationLoadBalancedFargateService CreateJuiceShop(ECS.ICluster ecsCluster, R53.IHostedZone zone)
        {
            var containerImage = _configuration.GetValue<string>("JuiceShop:ContainerImage");
            var desiredCount = _configuration.GetValue<int>("JuiceShop:DesiredCount");
            var domainName = _configuration.GetValue<string>("AWS:Route53:DomainName");
            var subdomainName = _configuration.GetValue<string>("JuiceShop:SubdomainName");
            var hostname = $"{subdomainName}.{domainName}";
            var certificateArn = _configuration.GetValue<string>("AWS:CertificateManager:CertificateArn");

            var juiceShop = new ECS.Patterns.ApplicationLoadBalancedFargateService(this, "JuiceShopService", new ECS.Patterns.ApplicationLoadBalancedFargateServiceProps
            {
                Certificate = CM.Certificate.FromCertificateArn(this, "Certificate", certificateArn),
                Cluster = ecsCluster,
                DesiredCount = desiredCount,
                DomainName = hostname,
                DomainZone = zone,
                Protocol = ELB.ApplicationProtocol.HTTPS,
                ServiceName = "JuiceShopService",
                TaskImageOptions = new ECS.Patterns.ApplicationLoadBalancedTaskImageOptions
                {
                    ContainerName = "JuiceShop",
                    ContainerPort = 3000,
                    Image = ECS.ContainerImage.FromRegistry(containerImage)
                }
            });

            return juiceShop;
        }

        // TODO: Pi-hole

        // TODO: WordPress
    }
}
